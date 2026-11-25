using System.Text.Json.Serialization;
using EJL.Data;
using EJL.Util;
using Microsoft.EntityFrameworkCore;

namespace EJL.Modules;

public static class AuthModule
{
    public static readonly Dictionary<string, string> RoleTitles = new()
    {
        ["admin"] = "Администратор",
        ["head"] = "Заведующий лабораторией",
        ["quality_manager"] = "Менеджер по качеству",
        ["engineer"] = "Инженер/лаборант",
    };

    public static void MapAuth(IEndpointRouteBuilder g)
    {
        g.MapPost("/login", async (
            LoginIn body, IDbContextFactory<EjlDbContext> dbf, JwtService jwt) =>
        {
            await using var db = await dbf.CreateDbContextAsync();
            var login = (body.Login ?? "").Trim().ToLowerInvariant();
            var u = await db.Users.FirstOrDefaultAsync(x => x.Login == login);
            if (u is null || !PasswordHasher.Verify(body.Password ?? "", u.PasswordHash) || !u.IsActive)
                throw new ApiException(401, "Неверный логин или пароль");
            var token = jwt.Issue(u.Id, u.Login, u.FullName, u.Role, u.CanWfPrecursors, TimeSpan.FromHours(12));
            Audit.Emit(db, "login", new CurrentUser(u.Id, u.Login, u.FullName, u.Role, u.CanWfPrecursors),
                "user", u.Id, u.FullName);
            await db.SaveChangesAsync();
            return Results.Json(new { token, user = Out(u) }, statusCode: 200);
        });

        g.MapGet("/me", async (HttpContext ctx, IDbContextFactory<EjlDbContext> dbf) =>
        {
            var current = ctx.GetUser();
            await using var db = await dbf.CreateDbContextAsync();
            var u = await db.Users.FindAsync(current.Id);
            if (u is null || !u.IsActive)
                throw new ApiException(403, "Пользователь недоступен");
            return Results.Json(Out(u));
        });

        g.MapGet("/users", async (HttpContext ctx, IDbContextFactory<EjlDbContext> dbf) =>
        {
            ctx.GetUser().RequireRole("admin", "head", "quality_manager");
            await using var db = await dbf.CreateDbContextAsync();
            var users = await db.Users.OrderBy(u => u.Id).ToListAsync();
            return Results.Json(users.Select(Out).ToList());
        });

        g.MapPost("/users", async (
            HttpContext ctx, UserCreate body, IDbContextFactory<EjlDbContext> dbf) =>
        {
            var current = ctx.GetUser();
            current.RequireRole("admin");
            if (body.Login is null || body.Login.Trim().Length is < 2 or > 64)
                throw new ApiException(422, "login: длина 2–64 символа");
            if (body.FullName is null || body.FullName.Trim().Length is < 2 or > 128)
                throw new ApiException(422, "full_name: длина 2–128 символов");
            if (string.IsNullOrEmpty(body.Password) || body.Password.Length < 4)
                throw new ApiException(422, "password: минимум 4 символа");
            if (body.Role is null || !RoleTitles.ContainsKey(body.Role))
                throw new ApiException(422,
                    $"Роль должна быть одной из: {string.Join(", ", RoleTitles.Keys.Order())}");

            await using var db = await dbf.CreateDbContextAsync();
            var login = body.Login.Trim().ToLowerInvariant();
            if (await db.Users.AnyAsync(x => x.Login == login))
                throw new ApiException(409, "Логин уже занят");
            var u = new User
            {
                Login = login,
                FullName = body.FullName.Trim(),
                PasswordHash = PasswordHasher.Hash(body.Password),
                Role = body.Role,
                CanWfPrecursors = body.CanWfPrecursors,
            };
            db.Users.Add(u);
            await db.SaveChangesAsync();
            Audit.Emit(db, "user.created", current, "user", u.Id, u.FullName,
                new() { ["role"] = u.Role, ["login"] = u.Login });
            await db.SaveChangesAsync();
            return Results.Json(Out(u), statusCode: 201);
        });

        g.MapPatch("/users/{userId:int}", async (
            HttpContext ctx, int userId, UserPatch body, IDbContextFactory<EjlDbContext> dbf) =>
        {
            var current = ctx.GetUser();
            current.RequireRole("admin");
            await using var db = await dbf.CreateDbContextAsync();
            var u = await db.Users.FindAsync(userId);
            if (u is null)
                throw new ApiException(404, "Пользователь не найден");
            if (body.Role is not null)
            {
                if (!RoleTitles.ContainsKey(body.Role))
                    throw new ApiException(422, "Недопустимая роль");
                u.Role = body.Role;
            }
            if (body.FullName is not null) u.FullName = body.FullName.Trim();
            if (body.CanWfPrecursors is not null) u.CanWfPrecursors = body.CanWfPrecursors.Value;
            if (body.IsActive is not null) u.IsActive = body.IsActive.Value;
            if (!string.IsNullOrEmpty(body.Password)) u.PasswordHash = PasswordHasher.Hash(body.Password);
            await db.SaveChangesAsync();
            Audit.Emit(db, "user.updated", current, "user", u.Id, u.FullName);
            await db.SaveChangesAsync();
            return Results.Json(Out(u));
        });
    }

    public static object Out(User u) => new
    {
        id = u.Id,
        login = u.Login,
        full_name = u.FullName,
        role = u.Role,
        role_title = RoleTitles.GetValueOrDefault(u.Role, u.Role),
        can_wf_precursors = u.CanWfPrecursors,
        is_active = u.IsActive,
    };
}

// ---------- DTO ----------

public sealed record LoginIn(string? Login, string? Password);

public sealed record UserCreate(
    string? Login,
    [property: JsonPropertyName("full_name")] string? FullName,
    string? Password,
    string? Role,
    [property: JsonPropertyName("can_wf_precursors")] bool CanWfPrecursors = false);

public sealed record UserPatch(
    [property: JsonPropertyName("full_name")] string? FullName,
    string? Role,
    [property: JsonPropertyName("can_wf_precursors")] bool? CanWfPrecursors,
    [property: JsonPropertyName("is_active")] bool? IsActive,
    string? Password);
