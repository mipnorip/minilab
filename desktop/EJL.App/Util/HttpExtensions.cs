using EJL.Data;
using Microsoft.EntityFrameworkCore;

namespace EJL.Util;

public static class HttpExtensions
{
    /// <summary>Текущий пользователь из заголовка Authorization (JWT).</summary>
    public static CurrentUser GetUser(this HttpContext ctx)
    {
        var auth = ctx.Request.Headers.Authorization.ToString();
        if (!auth.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
            throw new ApiException(401, "Требуется авторизация");
        var jwt = ctx.RequestServices.GetRequiredService<JwtService>();
        var user = jwt.Verify(auth["Bearer ".Length..].Trim());
        if (user is null)
            throw new ApiException(401, "Неверный или истёкший токен");
        return user;
    }

    public static void RequireRole(this CurrentUser user, params string[] roles)
    {
        if (!roles.Contains(user.Role))
            throw new ApiException(403, "Недостаточно прав");
    }
}

/// <summary>Запись событий в append-only журнал аудита (прямой вызов вместо REST-аудита микросервисов).</summary>
public static class Audit
{
    public static void Emit(
        EjlDbContext db, string action, CurrentUser actor, string entityType,
        object? entityId, string? entityTitle = null,
        Dictionary<string, object?>? payload = null, string? reason = null)
    {
        try
        {
            var id = entityId?.ToString() ?? "";
            db.AuditEvents.Add(new AuditEvent
            {
                Ts = DateTime.UtcNow,
                ActorId = actor.Id,
                ActorName = actor.Name ?? "system",
                Action = action,
                EntityType = entityType,
                EntityId = id.Length > 64 ? id[..64] : id,
                EntityTitle = entityTitle,
                Reason = reason,
                Payload = payload ?? new(),
            });
        }
        catch
        {
            /* «лучшее усилие»: аудит не роняет бизнес-операцию */
        }
    }
}

/// <summary>Пути приложения (регистрируются как singleton).</summary>
public sealed record AppPaths(string DataDir, string DbPath, string SecretPath, string LogPath, string UrlFile);
