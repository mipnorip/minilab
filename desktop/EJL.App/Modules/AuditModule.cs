using System.Globalization;
using System.Text.Json.Serialization;
using EJL.Data;
using EJL.Util;
using Microsoft.EntityFrameworkCore;

namespace EJL.Modules;

/// <summary>audit-svc: append-only журнал «кто — когда — что — почему». Изменение и удаление событий API не даёт.</summary>
public static class AuditModule
{
    public static void MapAudit(IEndpointRouteBuilder g)
    {
        g.MapPost("/events", async (
            HttpContext ctx, EventIn body, IDbContextFactory<EjlDbContext> dbf) =>
        {
            ctx.GetUser(); // любой вошедший (как в web-версии)
            await using var db = await dbf.CreateDbContextAsync();
            var e = new AuditEvent
            {
                ActorId = body.ActorId,
                ActorName = string.IsNullOrEmpty(body.ActorName) ? "system" : body.ActorName,
                Action = body.Action ?? "",
                EntityType = body.EntityType ?? "",
                EntityId = Truncate64(body.EntityId ?? ""),
                EntityTitle = body.EntityTitle,
                Reason = body.Reason,
                Payload = body.Payload ?? new(),
            };
            db.AuditEvents.Add(e);
            await db.SaveChangesAsync();
            return Results.Json(Out(e), statusCode: 201);
        });

        g.MapGet("/events", async (
            HttpContext ctx, IDbContextFactory<EjlDbContext> dbf,
            string? entity_type = null, string? entity_id = null, int? actor_id = null,
            string? action = null, string? date_from = null, string? date_to = null,
            int limit = 200) =>
        {
            var user = ctx.GetUser();
            if (user.Role is not ("admin" or "head" or "quality_manager"))
                throw new ApiException(403,
                    "Журнал аудита доступен руководителю, менеджеру по качеству и администратору");
            limit = Math.Min(limit, 1000);
            await using var db = await dbf.CreateDbContextAsync();
            var q = db.AuditEvents.AsNoTracking().AsQueryable();
            if (entity_type is not null) q = q.Where(e => e.EntityType == entity_type);
            if (entity_id is not null) q = q.Where(e => e.EntityId == entity_id);
            if (actor_id is not null) q = q.Where(e => e.ActorId == actor_id);
            if (action is not null) q = q.Where(e => e.Action == action);
            if (date_from is not null &&
                DateTime.TryParse(date_from + "T00:00:00", null, DateTimeStyles.None, out var fromTs))
                q = q.Where(e => e.Ts >= fromTs);
            if (date_to is not null &&
                DateTime.TryParse(date_to + "T23:59:59", null, DateTimeStyles.None, out var toTs))
                q = q.Where(e => e.Ts <= toTs);
            var rows = await q.OrderByDescending(e => e.Id).Take(limit).ToListAsync();
            return Results.Json(rows.Select(Out).ToList());
        });
    }

    private static string Truncate64(string s) => s.Length > 64 ? s[..64] : s;

    private static object Out(AuditEvent e) => new
    {
        id = e.Id,
        ts = e.Ts,
        actor_id = e.ActorId,
        actor_name = e.ActorName,
        action = e.Action,
        entity_type = e.EntityType,
        entity_id = e.EntityId,
        entity_title = e.EntityTitle,
        reason = e.Reason,
        payload = e.Payload,
    };
}

public sealed record EventIn(
    [property: JsonPropertyName("actor_id")] int? ActorId = null,
    [property: JsonPropertyName("actor_name")] string? ActorName = null,
    string? Action = null,
    [property: JsonPropertyName("entity_type")] string? EntityType = null,
    [property: JsonPropertyName("entity_id")] string? EntityId = null,
    [property: JsonPropertyName("entity_title")] string? EntityTitle = null,
    string? Reason = null,
    Dictionary<string, object?>? Payload = null);
