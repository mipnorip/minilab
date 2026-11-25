using EJL.Data;
using EJL.Util;
using Microsoft.EntityFrameworkCore;

namespace EJL.Modules;

/// <summary>scheduler-svc: напоминания (истекающие материалы, ежедневные записи, просроченные проверки).
/// Каждый вид уведомления создаётся не чаще раза в сутки (dedupe-ключ).</summary>
public static class NotificationsModule
{
    private const int ExpiringDays = 14;
    private const int DailyCheckHour = 12; // по местному времени ПК лаборатории

    public static void MapNotifications(IEndpointRouteBuilder g)
    {
        g.MapGet("/list", async (HttpContext ctx, IDbContextFactory<EjlDbContext> dbf, int limit = 100) =>
        {
            var user = ctx.GetUser();
            limit = Math.Min(limit, 500);
            await using var db = await dbf.CreateDbContextAsync();
            var rows = await db.Notifications.AsNoTracking()
                .Where(n => n.UserId == user.Id)
                .OrderBy(n => n.IsRead)
                .ThenByDescending(n => n.Id)
                .Take(limit)
                .ToListAsync();
            return Results.Json(rows.Select(NotifOut).ToList());
        });

        g.MapPost("/{notifId:long}/read", async (HttpContext ctx, long notifId, IDbContextFactory<EjlDbContext> dbf) =>
        {
            var user = ctx.GetUser();
            await using var db = await dbf.CreateDbContextAsync();
            var n = await db.Notifications.FindAsync(notifId);
            if (n is null || n.UserId != user.Id)
                throw new ApiException(404, "Уведомление не найдено");
            n.IsRead = true;
            await db.SaveChangesAsync();
            return Results.Json(NotifOut(n));
        });

        g.MapPost("/read-all", async (HttpContext ctx, IDbContextFactory<EjlDbContext> dbf) =>
        {
            var user = ctx.GetUser();
            await using var db = await dbf.CreateDbContextAsync();
            var unread = await db.Notifications
                .Where(n => n.UserId == user.Id && !n.IsRead)
                .ToListAsync();
            foreach (var n in unread) n.IsRead = true;
            await db.SaveChangesAsync();
            return Results.Json(new { ok = true });
        });

        g.MapPost("/tick", async (HttpContext ctx, IDbContextFactory<EjlDbContext> dbf) =>
        {
            var user = ctx.GetUser();
            if (user.Role is not ("admin" or "system"))
                throw new ApiException(403, "Проверку запускает администратор");
            var created = await RunChecks(dbf);
            return Results.Json(new { created });
        });
    }

    private static object NotifOut(Notification n) => new
    {
        id = n.Id,
        ts = n.Ts,
        type = n.Type,
        title = n.Title,
        body = n.Body,
        entity_type = n.EntityType,
        entity_id = n.EntityId,
        is_read = n.IsRead,
    };

    // ---------- логика проверок (порт run_checks) ----------

    public static async Task<int> RunChecks(IDbContextFactory<EjlDbContext> dbf)
    {
        var created = 0;
        await using var db = await dbf.CreateDbContextAsync();
        var users = await db.Users.AsNoTracking().Where(u => u.IsActive).ToListAsync();
        var staff = users.Where(u => u.Role is "admin" or "quality_manager" or "engineer").ToList();
        var managers = users.Where(u => u.Role is "admin" or "quality_manager").ToList();
        var today = DateOnly.FromDateTime(DateTime.Now).ToString("yyyy-MM-dd");

        // 1) истекающие материалы
        var cutoff = DateOnly.FromDateTime(DateTime.Today).AddDays(ExpiringDays).ToString("yyyy-MM-dd");
        var expiring = await db.Batches.AsNoTracking()
            .Where(b => b.Status == "active" && b.Qty > 0 && b.ExpiresOn != null)
            .ToListAsync();
        expiring = expiring.Where(b => string.Compare(b.ExpiresOn, cutoff) <= 0)
            .OrderBy(b => b.ExpiresOn)
            .ToList();
        var items = await db.Items.AsNoTracking().ToDictionaryAsync(i => i.Id);
        foreach (var b in expiring)
        {
            if (!items.TryGetValue(b.ItemId, out var it)) continue;
            var daysLeft = DateOnly.TryParseExact(b.ExpiresOn, "yyyy-MM-dd", out var d)
                ? d.DayNumber - DateOnly.FromDateTime(DateTime.Today).DayNumber : (int?)null;
            var when = daysLeft is < 0 ? "истёк" : $"осталось {daysLeft} дн.";
            foreach (var u in staff)
                created += Notify(db, u, "material_expiring",
                    $"Истекает срок годности: {it.Name}",
                    $"Партия {b.BatchNo} — {Math.Round(b.Qty, 3)} {it.Unit}, {when} (до {b.ExpiresOn}).",
                    "batch", b.Id, $"exp:{b.Id}:{today}");
        }

        // 2) ежедневные записи не внесены (после полудня по местному времени)
        if (DateTime.Now.Hour >= DailyCheckHour)
        {
            var dailies = await db.Journals.AsNoTracking().Where(j => j.Daily).ToListAsync();
            var todayStart = DateTime.Today;
            var todayEnd = DateTime.Today.AddDays(1);
            foreach (var j in dailies)
            {
                var hasToday = await db.Records.AsNoTracking()
                    .AnyAsync(r => r.JournalId == j.Id && r.CreatedAt >= todayStart && r.CreatedAt < todayEnd);
                if (hasToday) continue;
                var memberIds = await db.JournalMembers.AsNoTracking()
                    .Where(m => m.JournalId == j.Id)
                    .Select(m => m.UserId)
                    .ToListAsync();
                var targets = users.Where(u =>
                    memberIds.Contains(u.Id) || u.Role is "admin" or "quality_manager");
                foreach (var u in targets)
                    created += Notify(db, u, "daily_missing",
                        $"Не внесена запись: {j.Title}",
                        $"Журнал {j.Code} требует ежедневной записи за {today}.",
                        "journal", j.Id, $"daily:{j.Id}:{today}");
            }
        }

        // 3) просроченные проверки журналов
        var active = await db.Journals.AsNoTracking().Where(j => j.Status == "active").ToListAsync();
        var membersByJournal = await db.JournalMembers.AsNoTracking().ToListAsync();
        var now = DateTime.UtcNow;
        foreach (var j in active)
        {
            var due = (j.LastReviewAt ?? j.CreatedAt).AddDays(7 * j.CheckPeriodWeeks);
            if (due > now) continue;
            var memberIds = membersByJournal.Where(m => m.JournalId == j.Id)
                .Select(m => m.UserId).ToHashSet();
            var targets = users.Where(u => memberIds.Contains(u.Id) || managers.Contains(u));
            var since = j.LastReviewAt?.ToString("yyyy-MM-dd") ?? "создания";
            foreach (var u in targets)
                created += Notify(db, u, "review_due",
                    $"Просрочена проверка журнала: {j.Code}",
                    $"Журнал «{j.Title}» не проверялся с {since}. " +
                    $"Периодичность — {j.CheckPeriodWeeks} нед.",
                    "journal", j.Id, $"review:{j.Id}:{today}");
        }

        await db.SaveChangesAsync();
        return created;
    }

    private static int Notify(
        EjlDbContext db, User u, string type, string title, string body,
        string? entityType, object? entityId, string dedupeKey)
    {
        var key = $"{dedupeKey}:u{u.Id}";
        if (db.Notifications.Any(n => n.DedupeKey == key)) return 0;
        db.Notifications.Add(new Notification
        {
            UserId = u.Id,
            UserName = u.FullName,
            Type = type,
            Title = title.Length > 256 ? title[..256] : title,
            Body = body.Length > 512 ? body[..512] : body,
            EntityType = entityType,
            EntityId = entityId?.ToString(),
            DedupeKey = key,
        });
        return 1;
    }
}
