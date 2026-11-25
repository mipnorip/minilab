using System.Globalization;
using System.Text.Json.Serialization;
using EJL.Data;
using EJL.Domain;
using EJL.Util;
using Microsoft.EntityFrameworkCore;

namespace EJL.Modules;

/// <summary>records-svc: реестр журналов, записи, исправления «с причиной», проверки — правила ГОСТ 17025.</summary>
public static class RecordsModule
{
    private static readonly Dictionary<string, string> ReasonCodes = new()
    {
        ["V"] = "ошибка вычисления",
        ["O"] = "опечатка/ошибочные данные",
        ["I"] = "иное",
    };

    public static void MapRecords(IEndpointRouteBuilder g)
    {
        // ── журналы ─────────────────────────────────────────────────────────

        g.MapGet("/journals", async (
            HttpContext ctx, IDbContextFactory<EjlDbContext> dbf, bool? daily = null) =>
        {
            var user = ctx.GetUser();
            await using var db = await dbf.CreateDbContextAsync();
            var journals = await db.Journals.AsNoTracking().OrderBy(j => j.Code).ToListAsync();
            var membersByJournal = await db.JournalMembers.AsNoTracking().ToListAsync();
            var result = new List<Dictionary<string, object?>>();
            foreach (var j in journals)
            {
                if (daily is not null && j.Daily != daily) continue;
                var members = membersByJournal.Where(m => m.JournalId == j.Id).ToList();
                if (!CanView(user, j, members)) continue;
                var out1 = JournalOut(j, members);
                out1["is_member"] = IsMember(user, members);
                result.Add(out1);
            }
            return Results.Json(result);
        });

        g.MapGet("/journals/review-due", async (
            HttpContext ctx, IDbContextFactory<EjlDbContext> dbf, string? before = null) =>
        {
            var user = ctx.GetUser();
            if (user.Role is not ("system" or "quality_manager" or "admin"))
                throw new ApiException(403, "Недостаточно прав");
            var cutoff = ParseIso(before ?? DateTime.UtcNow.ToString("O"));
            await using var db = await dbf.CreateDbContextAsync();
            var journals = await db.Journals.AsNoTracking().Where(j => j.Status == "active").ToListAsync();
            var membersByJournal = await db.JournalMembers.AsNoTracking().ToListAsync();
            var result = new List<Dictionary<string, object?>>();
            foreach (var j in journals)
            {
                var base_ = (j.LastReviewAt ?? j.CreatedAt).AddDays(7 * j.CheckPeriodWeeks);
                if (base_ > cutoff) continue;
                var members = membersByJournal.Where(m => m.JournalId == j.Id).ToList();
                var item = JournalOut(j, members);
                item["members"] = members.Select(MemberOut).ToList();
                result.Add(item);
            }
            return Results.Json(result);
        });

        g.MapGet("/journals/code/{**code}", async (
            HttpContext ctx, string code, IDbContextFactory<EjlDbContext> dbf) =>
        {
            var user = ctx.GetUser();
            await using var db = await dbf.CreateDbContextAsync();
            var j = await db.Journals.AsNoTracking().FirstOrDefaultAsync(x => x.Code == code);
            if (j is null)
                throw new ApiException(404, "Журнал не найден");
            var members = await CheckView(db, user, j);
            var out1 = JournalOut(j, members, withMembers: true);
            out1["is_member"] = IsMember(user, members);
            return Results.Json(out1);
        });

        g.MapGet("/journals/{journalId:int}", async (
            HttpContext ctx, int journalId, IDbContextFactory<EjlDbContext> dbf) =>
        {
            var user = ctx.GetUser();
            await using var db = await dbf.CreateDbContextAsync();
            var j = await GetJournalOr404(db, journalId);
            var members = await CheckView(db, user, j);
            var out1 = JournalOut(j, members, withMembers: true);
            out1["is_member"] = IsMember(user, members);
            return Results.Json(out1);
        });

        g.MapPost("/journals", async (
            HttpContext ctx, JournalCreate body, IDbContextFactory<EjlDbContext> dbf) =>
        {
            var user = ctx.GetUser();
            if (user.Role != "admin")
                throw new ApiException(403, "Журналы создаёт только администратор");
            if (body.Kind is not ("form" or "materials"))
                throw new ApiException(422, "kind должен быть form или materials");

            await using var db = await dbf.CreateDbContextAsync();
            int? formVersionId = null;
            if (body.Kind == "form")
            {
                if (body.FormTemplateId is not > 0)
                    throw new ApiException(422, "Для журнала с формой укажите form_template_id");
                formVersionId = await ResolveCurrentVersion(db, body.FormTemplateId.Value);
            }
            else if (body.MaterialsType is not ("reagent" or "so" or "precursor"))
            {
                throw new ApiException(422, "materials_type должен быть reagent, so или precursor");
            }

            var code = (body.Code ?? "").Trim();
            if (code.Length == 0)
                throw new ApiException(422, "Укажите шифр журнала");
            if (await db.Journals.AnyAsync(j => j.Code == code))
                throw new ApiException(409, "Журнал с таким шифром уже существует");

            var j = new Journal
            {
                Code = code,
                Title = (body.Title ?? "").Trim(),
                Kind = body.Kind,
                FormTemplateId = body.FormTemplateId,
                FormVersionId = formVersionId,
                MaterialsType = body.MaterialsType,
                OwnerId = body.OwnerId,
                OwnerName = (body.OwnerName ?? "").Trim(),
                CheckPeriodWeeks = body.CheckPeriodWeeks,
                RetentionYears = body.RetentionYears,
                Daily = body.Daily,
            };

            // участники: дедупликация по user_id, ответственный добавляется автоматически
            var memberMap = (body.Members ?? []).GroupBy(m => m.UserId).ToDictionary(x => x.Key, x => x.First());
            memberMap[body.OwnerId] = new MemberIn(body.OwnerId, (body.OwnerName ?? "").Trim(), "owner");
            foreach (var m in memberMap.Values)
                if (m.Role is not ("owner" or "worker"))
                    throw new ApiException(422, "Роль участника: owner или worker");

            db.Journals.Add(j);
            await db.SaveChangesAsync();
            foreach (var m in memberMap.Values)
            {
                db.JournalMembers.Add(new JournalMember
                {
                    JournalId = j.Id, UserId = m.UserId, Name = m.Name.Trim(), Role = m.Role,
                });
            }
            Audit.Emit(db, "journal.created", user, "journal", j.Id, $"{j.Code} {j.Title}",
                new() { ["kind"] = j.Kind, ["check_period_weeks"] = j.CheckPeriodWeeks });
            await db.SaveChangesAsync();
            var members = await db.JournalMembers.AsNoTracking().Where(m => m.JournalId == j.Id).ToListAsync();
            return Results.Json(JournalOut(j, members, withMembers: true), statusCode: 201);
        });

        g.MapPatch("/journals/{journalId:int}", async (
            HttpContext ctx, int journalId, JournalPatch body, IDbContextFactory<EjlDbContext> dbf) =>
        {
            var user = ctx.GetUser();
            if (user.Role != "admin")
                throw new ApiException(403, "Журналы настраивает только администратор");
            await using var db = await dbf.CreateDbContextAsync();
            var j = await GetJournalOr404(db, journalId);
            if (body.Status is not null)
            {
                if (body.Status is not ("active" or "closed" or "archived"))
                    throw new ApiException(422, "Статус: active, closed или archived");
                j.Status = body.Status;
            }
            if (body.Title is not null) j.Title = body.Title.Trim();
            if (body.CheckPeriodWeeks is not null) j.CheckPeriodWeeks = body.CheckPeriodWeeks.Value;
            if (body.RetentionYears is not null) j.RetentionYears = body.RetentionYears.Value;
            if (body.Daily is not null) j.Daily = body.Daily.Value;
            if (body.OwnerId is not null && body.OwnerName is not null)
            {
                j.OwnerId = body.OwnerId.Value;
                j.OwnerName = body.OwnerName.Trim();
            }
            if (body.FormVersionId is not null) j.FormVersionId = body.FormVersionId;

            if (body.Members is not null)
            {
                var old = await db.JournalMembers.Where(m => m.JournalId == j.Id).ToListAsync();
                db.JournalMembers.RemoveRange(old);
                var memberMap = body.Members.GroupBy(m => m.UserId).ToDictionary(x => x.Key, x => x.First());
                var ownerId = body.OwnerId ?? j.OwnerId;
                var ownerName = body.OwnerId is not null && body.OwnerName is not null
                    ? body.OwnerName.Trim() : j.OwnerName;
                memberMap[ownerId] = new MemberIn(ownerId, ownerName, "owner");
                foreach (var m in memberMap.Values)
                {
                    if (m.Role is not ("owner" or "worker"))
                        throw new ApiException(422, "Роль участника: owner или worker");
                    db.JournalMembers.Add(new JournalMember
                    {
                        JournalId = j.Id, UserId = m.UserId, Name = m.Name.Trim(), Role = m.Role,
                    });
                }
            }
            Audit.Emit(db, "journal.updated", user, "journal", j.Id, $"{j.Code} {j.Title}",
                new() { ["status"] = j.Status });
            await db.SaveChangesAsync();
            var members = await db.JournalMembers.AsNoTracking().Where(m => m.JournalId == j.Id).ToListAsync();
            return Results.Json(JournalOut(j, members, withMembers: true));
        });

        // ── записи ─────────────────────────────────────────────────────────

        g.MapGet("/journals/{journalId:int}/records", async (
            HttpContext ctx, int journalId, IDbContextFactory<EjlDbContext> dbf,
            string? date_from = null, string? date_to = null, string? status = null,
            int? author_id = null, int limit = 200, int offset = 0) =>
        {
            var user = ctx.GetUser();
            limit = Math.Min(limit, 1000);
            await using var db = await dbf.CreateDbContextAsync();
            var j = await GetJournalOr404(db, journalId);
            await CheckView(db, user, j);

            var q = db.Records.AsNoTracking().Where(r => r.JournalId == journalId);
            if (date_from is not null && TryParseIso(date_from + "T00:00:00Z", out var fromTs))
                q = q.Where(r => r.CreatedAt >= fromTs);
            if (date_to is not null && TryParseIso(date_to + "T23:59:59Z", out var toTs))
                q = q.Where(r => r.CreatedAt <= toTs);
            if (status is not null)
                q = q.Where(r => r.Status == status);
            if (author_id is not null)
                q = q.Where(r => r.AuthorId == author_id);
            var rows = await q.OrderByDescending(r => r.RowNo).Skip(offset).Take(limit).ToListAsync();
            return Results.Json(rows.Select(r => RecordOut(r, j.Code)).ToList());
        });

        g.MapPost("/journals/{journalId:int}/records", async (
            HttpContext ctx, int journalId, RecordCreate body, IDbContextFactory<EjlDbContext> dbf) =>
        {
            var user = ctx.GetUser();
            await using var db = await dbf.CreateDbContextAsync();
            var j = await GetJournalOr404(db, journalId);
            var members = await CheckView(db, user, j);
            if (j.Kind != "form")
                throw new ApiException(422,
                    "Записи вносятся только в журналы с формой; журналы материалов ведёт модуль материалов");
            if (j.Status != "active")
                throw new ApiException(409,
                    j.Status == "closed" ? "Журнал закрыт, новые записи не вносятся" : "Журнал в архиве, только чтение");
            if (!CanWrite(user, j, members))
                throw new ApiException(403, "Нет прав на внесение записей в этот журнал");

            var columns = await GetColumns(db, j.FormTemplateId, j.FormVersionId);
            var last = await db.Records.AsNoTracking()
                .Where(r => r.JournalId == j.Id).OrderByDescending(r => r.RowNo)
                .FirstOrDefaultAsync();
            var rowNo = last?.RowNo + 1 ?? 1;
            var data = Columns.Apply(columns, body.Data, db, user, rowNo);

            var r = new RecordEntity
            {
                JournalId = j.Id,
                RowNo = rowNo,
                Data = data,
                AuthorId = user.Id,
                AuthorName = user.Name ?? "",
            };
            db.Records.Add(r);
            await db.SaveChangesAsync();
            Audit.Emit(db, "record.created", user, "record", r.Id, $"{j.Code} №{rowNo}",
                new() { ["journal_code"] = j.Code, ["row_no"] = rowNo });
            await db.SaveChangesAsync();
            return Results.Json(RecordOut(r, j.Code), statusCode: 201);
        });

        g.MapGet("/records/{recordId:long}", async (
            HttpContext ctx, long recordId, IDbContextFactory<EjlDbContext> dbf) =>
        {
            var user = ctx.GetUser();
            await using var db = await dbf.CreateDbContextAsync();
            var r = await db.Records.FindAsync(recordId);
            if (r is null)
                throw new ApiException(404, "Запись не найдена");
            var j = await db.Journals.FindAsync(r.JournalId) ?? throw new ApiException(404, "Журнал не найден");
            await CheckView(db, user, j);
            return Results.Json(RecordOut(r, j.Code));
        });

        g.MapPatch("/records/{recordId:long}", async (
            HttpContext ctx, long recordId, RecordCorrect body, IDbContextFactory<EjlDbContext> dbf) =>
        {
            var user = ctx.GetUser();
            if (body.ReasonCode is null || !ReasonCodes.ContainsKey(body.ReasonCode))
                throw new ApiException(422, "Код причины: V (ошибка вычисления), O (опечатка) или I (иное)");
            await using var db = await dbf.CreateDbContextAsync();
            var r = await db.Records.FindAsync(recordId);
            if (r is null)
                throw new ApiException(404, "Запись не найдена");
            var j = await db.Journals.FindAsync(r.JournalId) ?? throw new ApiException(404, "Журнал не найден");
            var members = await CheckView(db, user, j);
            if (j.Status == "archived")
                throw new ApiException(409, "Журнал в архиве: записи неизменяемы");
            if (r.Status != "entered")
                throw new ApiException(409, "Закрытая запись не исправляется");
            var allowed = user.Role == "admin" || r.AuthorId == user.Id
                || members.Any(m => m.UserId == user.Id && m.Role == "owner");
            if (!allowed)
                throw new ApiException(403,
                    "Исправлять запись может её автор, ответственный за журнал или администратор");

            var columns = await GetColumns(db, j.FormTemplateId, j.FormVersionId);
            var newData = Columns.Apply(columns, body.Data, db, user, r.RowNo, oldData: r.Data);

            var oldData = new Dictionary<string, object?>(r.Data); // прежнее значение — в ревизию
            db.Revisions.Add(new Revision
            {
                RecordId = r.Id,
                OldData = oldData,
                NewData = newData,
                ReasonCode = body.ReasonCode,
                ReasonComment = body.ReasonComment,
                AuthorId = user.Id,
                AuthorName = user.Name ?? "",
            });
            r.Data = newData;
            r.UpdatedAt = DateTime.UtcNow;
            var reasonText = ReasonCodes[body.ReasonCode] +
                (string.IsNullOrEmpty(body.ReasonComment) ? "" : $": {body.ReasonComment}");
            Audit.Emit(db, "record.corrected", user, "record", r.Id, $"{j.Code} №{r.RowNo}",
                new() { ["journal_code"] = j.Code, ["row_no"] = r.RowNo }, reason: reasonText);
            await db.SaveChangesAsync();
            return Results.Json(RecordOut(r, j.Code));
        });

        g.MapPost("/records/{recordId:long}/close", async (
            HttpContext ctx, long recordId, IDbContextFactory<EjlDbContext> dbf) =>
        {
            var user = ctx.GetUser();
            await using var db = await dbf.CreateDbContextAsync();
            var r = await db.Records.FindAsync(recordId);
            if (r is null)
                throw new ApiException(404, "Запись не найдена");
            var j = await db.Journals.FindAsync(r.JournalId) ?? throw new ApiException(404, "Журнал не найден");
            var members = await CheckView(db, user, j);
            if (j.Status == "archived")
                throw new ApiException(409, "Журнал в архиве");
            if (!CanClose(user, j, members))
                throw new ApiException(403, "Закрыть запись может ответственный за журнал или администратор");
            r.Status = "closed";
            r.UpdatedAt = DateTime.UtcNow;
            Audit.Emit(db, "record.closed", user, "record", r.Id, $"{j.Code} №{r.RowNo}",
                new() { ["journal_code"] = j.Code, ["row_no"] = r.RowNo });
            await db.SaveChangesAsync();
            return Results.Json(RecordOut(r, j.Code));
        });

        g.MapGet("/records/{recordId:long}/history", async (
            HttpContext ctx, long recordId, IDbContextFactory<EjlDbContext> dbf) =>
        {
            var user = ctx.GetUser();
            await using var db = await dbf.CreateDbContextAsync();
            var r = await db.Records.FindAsync(recordId);
            if (r is null)
                throw new ApiException(404, "Запись не найдена");
            var j = await db.Journals.FindAsync(r.JournalId) ?? throw new ApiException(404, "Журнал не найден");
            await CheckView(db, user, j);
            var revs = await db.Revisions.AsNoTracking()
                .Where(rv => rv.RecordId == recordId)
                .OrderBy(rv => rv.Id)
                .ToListAsync();
            return Results.Json(new
            {
                record = RecordOut(r, j.Code),
                revisions = revs.Select(RevisionOut).ToList(),
            });
        });

        // ── проверки журналов ──────────────────────────────────────────────

        g.MapPost("/journals/{journalId:int}/review", async (
            HttpContext ctx, int journalId, ReviewIn body, IDbContextFactory<EjlDbContext> dbf) =>
        {
            var user = ctx.GetUser();
            if (user.Role is not ("quality_manager" or "admin"))
                throw new ApiException(403,
                    "Проверку журнала отмечает менеджер по качеству или администратор");
            await using var db = await dbf.CreateDbContextAsync();
            var j = await GetJournalOr404(db, journalId);
            await CheckView(db, user, j);
            if (j.Status == "archived")
                throw new ApiException(409, "Журнал в архиве не проверяется");
            var log = new ReviewLog
            {
                JournalId = j.Id,
                ReviewerId = user.Id,
                ReviewerName = user.Name ?? "",
                Note = body.Note,
            };
            db.ReviewLogs.Add(log);
            j.LastReviewAt = DateTime.UtcNow;
            Audit.Emit(db, "journal.reviewed", user, "journal", j.Id, $"{j.Code} {j.Title}",
                new() { ["note"] = body.Note });
            await db.SaveChangesAsync();
            return Results.Json(new
            {
                id = log.Id,
                journal_id = log.JournalId,
                reviewer_name = log.ReviewerName,
                note = log.Note,
                ts = log.Ts,
            }, statusCode: 201);
        });

        g.MapGet("/journals/{journalId:int}/reviews", async (
            HttpContext ctx, int journalId, IDbContextFactory<EjlDbContext> dbf) =>
        {
            var user = ctx.GetUser();
            await using var db = await dbf.CreateDbContextAsync();
            var j = await GetJournalOr404(db, journalId);
            await CheckView(db, user, j);
            var logs = await db.ReviewLogs.AsNoTracking()
                .Where(l => l.JournalId == journalId)
                .OrderByDescending(l => l.Id)
                .Take(50)
                .ToListAsync();
            return Results.Json(logs.Select(l => new
            {
                id = l.Id,
                reviewer_name = l.ReviewerName,
                note = l.Note,
                ts = l.Ts,
            }).ToList());
        });
    }

    // ---------- права (порт can_view / can_write / can_close) ----------

    private static bool IsMember(CurrentUser user, List<JournalMember> members) =>
        members.Any(m => m.UserId == user.Id);

    private static bool CanView(CurrentUser user, Journal j, List<JournalMember> members) =>
        user.Role is "admin" or "head" or "quality_manager" or "system"
        || j.Kind == "materials"
        || IsMember(user, members);

    private static bool CanWrite(CurrentUser user, Journal j, List<JournalMember> members) =>
        user.Role == "admin"
        || (user.Role == "engineer" && IsMember(user, members));

    private static bool CanClose(CurrentUser user, Journal j, List<JournalMember> members) =>
        user.Role == "admin"
        || (user.Role == "engineer" && members.Any(m => m.UserId == user.Id && m.Role == "owner"));

    private static async Task<Journal> GetJournalOr404(EjlDbContext db, int journalId)
    {
        var j = await db.Journals.FindAsync(journalId);
        if (j is null)
            throw new ApiException(404, "Журнал не найден");
        return j;
    }

    private static async Task<List<JournalMember>> CheckView(EjlDbContext db, CurrentUser user, Journal j)
    {
        var members = await db.JournalMembers.AsNoTracking()
            .Where(m => m.JournalId == j.Id).ToListAsync();
        if (!CanView(user, j, members))
            throw new ApiException(403, "Нет доступа к журналу");
        return members;
    }

    // ---------- колонки формы (вместо HTTP-вызова в forms-svc — прямой запрос) ----------

    public static async Task<List<FormColumn>> GetColumns(EjlDbContext db, int? templateId, int? versionId)
    {
        if (templateId is null || versionId is null)
            throw new ApiException(502, "Не удалось получить схему формы");
        var v = await db.TemplateVersions.AsNoTracking()
            .FirstOrDefaultAsync(x => x.Id == versionId);
        if (v is null)
            throw new ApiException(502, "Не удалось получить схему формы");
        return v.Columns;
    }

    public static async Task<int> ResolveCurrentVersion(EjlDbContext db, int templateId)
    {
        var t = await db.Templates.FindAsync(templateId);
        if (t is null)
            throw new ApiException(404, "Шаблон формы не найден");
        if (t.CurrentVersionId is not { } vid)
            throw new ApiException(422, "У шаблона нет версий");
        return vid;
    }

    // ---------- сериализация ----------

    public static Dictionary<string, object?> JournalOut(
        Journal j, List<JournalMember> members, bool withMembers = false)
    {
        var nextReview = (j.LastReviewAt ?? j.CreatedAt).AddDays(7 * j.CheckPeriodWeeks);
        var out1 = new Dictionary<string, object?>
        {
            ["id"] = j.Id,
            ["code"] = j.Code,
            ["title"] = j.Title,
            ["kind"] = j.Kind,
            ["form_template_id"] = j.FormTemplateId,
            ["form_version_id"] = j.FormVersionId,
            ["materials_type"] = j.MaterialsType,
            ["owner_id"] = j.OwnerId,
            ["owner_name"] = j.OwnerName,
            ["check_period_weeks"] = j.CheckPeriodWeeks,
            ["retention_years"] = j.RetentionYears,
            ["status"] = j.Status,
            ["daily"] = j.Daily,
            ["last_review_at"] = j.LastReviewAt,
            ["next_review_at"] = nextReview,
        };
        if (withMembers)
            out1["members"] = members.Select(MemberOut).ToList();
        return out1;
    }

    private static object MemberOut(JournalMember m) => new
    {
        user_id = m.UserId,
        name = m.Name,
        role = m.Role,
    };

    public static object RecordOut(RecordEntity r, string? journalCode = null) => new
    {
        id = r.Id,
        journal_id = r.JournalId,
        journal_code = journalCode,
        row_no = r.RowNo,
        data = r.Data,
        status = r.Status,
        author_id = r.AuthorId,
        author_name = r.AuthorName,
        created_at = r.CreatedAt,
        updated_at = r.UpdatedAt,
    };

    private static object RevisionOut(Revision rv) => new
    {
        id = rv.Id,
        record_id = rv.RecordId,
        old_data = rv.OldData,
        new_data = rv.NewData,
        reason_code = rv.ReasonCode,
        reason_comment = rv.ReasonComment,
        author_name = rv.AuthorName,
        ts = rv.Ts,
    };

    private static DateTime ParseIso(string iso)
    {
        if (TryParseIso(iso, out var dt)) return dt;
        throw new ApiException(422, "Некорректная дата");
    }

    private static bool TryParseIso(string? iso, out DateTime dt) =>
        DateTime.TryParse(iso, null, DateTimeStyles.RoundtripKind, out dt) ||
        DateTime.TryParse(iso, null, DateTimeStyles.None, out dt);
}

// ---------- DTO ----------

public sealed record MemberIn(
    [property: JsonPropertyName("user_id")] int UserId,
    string Name,
    string Role = "worker");

public sealed record JournalCreate(
    string? Code,
    string? Title,
    string? Kind,
    [property: JsonPropertyName("form_template_id")] int? FormTemplateId,
    [property: JsonPropertyName("materials_type")] string? MaterialsType,
    [property: JsonPropertyName("owner_id")] int OwnerId,
    [property: JsonPropertyName("owner_name")] string? OwnerName,
    List<MemberIn>? Members = null,
    [property: JsonPropertyName("check_period_weeks")] int CheckPeriodWeeks = 4,
    [property: JsonPropertyName("retention_years")] int RetentionYears = 5,
    bool Daily = false);

public sealed record JournalPatch(
    string? Title = null,
    [property: JsonPropertyName("owner_id")] int? OwnerId = null,
    [property: JsonPropertyName("owner_name")] string? OwnerName = null,
    List<MemberIn>? Members = null,
    string? Status = null,
    [property: JsonPropertyName("check_period_weeks")] int? CheckPeriodWeeks = null,
    [property: JsonPropertyName("retention_years")] int? RetentionYears = null,
    bool? Daily = null,
    [property: JsonPropertyName("form_version_id")] int? FormVersionId = null);

public sealed record RecordCreate(Dictionary<string, object?>? Data = null);

public sealed record RecordCorrect(
    Dictionary<string, object?>? Data = null,
    [property: JsonPropertyName("reason_code")] string? ReasonCode = null,
    [property: JsonPropertyName("reason_comment")] string? ReasonComment = null);

public sealed record ReviewIn(string? Note = null);
