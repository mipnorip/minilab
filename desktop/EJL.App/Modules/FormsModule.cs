using System.Globalization;
using System.Text.RegularExpressions;
using System.Text.Json;
using System.Text.Json.Serialization;
using EJL.Data;
using EJL.Domain;
using EJL.Util;
using Microsoft.EntityFrameworkCore;

namespace EJL.Modules;

public static class FormsModule
{
    private static readonly HashSet<string> ColumnTypes =
        ["text", "number", "date", "bool", "enum", "ref_journal", "ref_material", "formula", "auto"];
    private static readonly HashSet<string> AutoKinds = ["row", "date", "author"];

    public static void MapForms(IEndpointRouteBuilder g)
    {
        g.MapGet("/templates", async (HttpContext ctx, IDbContextFactory<EjlDbContext> dbf, bool presets_only = false) =>
        {
            ctx.GetUser();
            await using var db = await dbf.CreateDbContextAsync();
            var q = db.Templates.AsNoTracking().OrderBy(t => t.Code);
            var templates = presets_only
                ? await q.Where(t => t.IsPreset).ToListAsync()
                : await q.ToListAsync();
            return Results.Json(templates.Select(t => TemplateOut(db, t)).ToList());
        });

        g.MapGet("/templates/{templateId:int}", async (HttpContext ctx, int templateId, IDbContextFactory<EjlDbContext> dbf) =>
        {
            ctx.GetUser();
            await using var db = await dbf.CreateDbContextAsync();
            var t = await db.Templates.FindAsync(templateId);
            if (t is null)
                throw new ApiException(404, "Шаблон не найден");
            var out1 = TemplateOut(db, t);
            if (t.CurrentVersionId is { } vid)
            {
                var v = await db.TemplateVersions.FindAsync(vid);
                if (v is not null)
                    out1["columns"] = v.Columns;
            }
            return Results.Json(out1);
        });

        g.MapGet("/templates/{templateId:int}/versions", async (HttpContext ctx, int templateId, IDbContextFactory<EjlDbContext> dbf) =>
        {
            ctx.GetUser();
            await using var db = await dbf.CreateDbContextAsync();
            if (await db.Templates.FindAsync(templateId) is null)
                throw new ApiException(404, "Шаблон не найден");
            var vs = await db.TemplateVersions.AsNoTracking()
                .Where(v => v.TemplateId == templateId)
                .OrderBy(v => v.Version)
                .ToListAsync();
            return Results.Json(vs.Select(v => VersionOut(v)).ToList());
        });

        g.MapGet("/templates/{templateId:int}/versions/{versionId:int}", async (HttpContext ctx, int templateId, int versionId, IDbContextFactory<EjlDbContext> dbf) =>
        {
            ctx.GetUser();
            await using var db = await dbf.CreateDbContextAsync();
            var v = await db.TemplateVersions.FindAsync(versionId);
            if (v is null || v.TemplateId != templateId)
                throw new ApiException(404, "Версия не найдена");
            return Results.Json(VersionOut(v, withColumns: true));
        });

        g.MapPost("/templates", async (HttpContext ctx, TemplateCreate body, IDbContextFactory<EjlDbContext> dbf) =>
        {
            var user = ctx.GetUser();
            user.RequireRole("admin");
            var columns = ValidateColumns(body.Columns);
            if (body.Code is null || body.Code.Trim().Length is < 2 or > 32)
                throw new ApiException(422, "code: длина 2–32 символа");
            if (body.Title is null || body.Title.Trim().Length is < 2 or > 128)
                throw new ApiException(422, "title: длина 2–128 символов");

            await using var db = await dbf.CreateDbContextAsync();
            var code = body.Code.Trim().ToUpperInvariant();
            if (await db.Templates.AnyAsync(t => t.Code == code))
                throw new ApiException(409, "Код шаблона уже занят");
            var t = new Template { Code = code, Title = body.Title.Trim(), IsPreset = body.IsPreset };
            db.Templates.Add(t);
            await db.SaveChangesAsync();
            var v = new TemplateVersion { TemplateId = t.Id, Version = 1, Columns = columns, CreatedBy = user.Id };
            db.TemplateVersions.Add(v);
            await db.SaveChangesAsync();
            t.CurrentVersionId = v.Id;
            Audit.Emit(db, "form.created", user, "form", t.Id, $"{t.Code} {t.Title}",
                new() { ["columns"] = columns.Count });
            await db.SaveChangesAsync();
            return Results.Json(TemplateOut(db, t), statusCode: 201);
        });

        g.MapPost("/templates/{templateId:int}/versions", async (HttpContext ctx, int templateId, VersionCreate body, IDbContextFactory<EjlDbContext> dbf) =>
        {
            var user = ctx.GetUser();
            user.RequireRole("admin");
            var columns = ValidateColumns(body.Columns);
            await using var db = await dbf.CreateDbContextAsync();
            var t = await db.Templates.FindAsync(templateId);
            if (t is null)
                throw new ApiException(404, "Шаблон не найден");
            var last = await db.TemplateVersions.AsNoTracking()
                .Where(v => v.TemplateId == templateId)
                .OrderByDescending(v => v.Version)
                .FirstOrDefaultAsync();
            var v = new TemplateVersion
            {
                TemplateId = templateId,
                Version = last?.Version + 1 ?? 1,
                Columns = columns,
                Comment = body.Comment,
                CreatedBy = user.Id,
            };
            db.TemplateVersions.Add(v);
            await db.SaveChangesAsync();
            t.CurrentVersionId = v.Id;
            Audit.Emit(db, "form.versioned", user, "form", t.Id, $"{t.Code} {t.Title}",
                new() { ["version"] = v.Version, ["columns"] = columns.Count });
            await db.SaveChangesAsync();
            return Results.Json(VersionOut(v, withColumns: true), statusCode: 201);
        });
    }

    // ---------- сериализация ----------

    private static Dictionary<string, object?> TemplateOut(EjlDbContext db, Template t)
    {
        int? versionNo = null;
        if (t.CurrentVersionId is { } vid)
            versionNo = db.TemplateVersions.Find(vid)?.Version;
        return new Dictionary<string, object?>
        {
            ["id"] = t.Id,
            ["code"] = t.Code,
            ["title"] = t.Title,
            ["is_preset"] = t.IsPreset,
            ["current_version_id"] = t.CurrentVersionId,
            ["version"] = versionNo,
        };
    }

    private static object VersionOut(TemplateVersion v, bool withColumns = false)
    {
        var dict = new Dictionary<string, object?>
        {
            ["id"] = v.Id,
            ["template_id"] = v.TemplateId,
            ["version"] = v.Version,
            ["comment"] = v.Comment,
            ["created_at"] = v.CreatedAt,
        };
        if (withColumns) dict["columns"] = v.Columns;
        return dict;
    }

    // ---------- валидация колонок (порт validate_columns) ----------

    public static List<FormColumn> ValidateColumns(List<Dictionary<string, object?>>? columns)
    {
        if (columns is null || columns.Count == 0)
            throw new ApiException(422, "Шаблон должен содержать хотя бы одну колонку");

        var keys = new HashSet<string>();
        var result = new List<FormColumn>();
        foreach (var c in columns)
        {
            var key = GetStr(c, "key") ?? "";
            if (!Regex.IsMatch(key, @"^[a-z_][a-z0-9_]*$"))
                throw new ApiException(422, $"Ключ колонки «{key}»: латиница в нижнем регистре, цифры, _");
            if (!keys.Add(key))
                throw new ApiException(422, $"Дублирующийся ключ колонки: {key}");
            var title = (GetStr(c, "title") ?? "").Trim();
            if (title.Length == 0)
                throw new ApiException(422, $"Колонка «{key}»: не задано название");
            var type = GetStr(c, "type") ?? "";
            if (!ColumnTypes.Contains(type))
                throw new ApiException(422, $"Колонка «{title}»: неизвестный тип {type}");
            var options = GetList(c, "options");
            if (type == "enum" && (options is null || options.Count == 0))
                throw new ApiException(422, $"Колонка «{title}»: задайте варианты списка");
            var refCode = (GetStr(c, "ref_code") ?? "").Trim();
            if (type == "ref_journal" && refCode.Length == 0)
                throw new ApiException(422, $"Колонка «{title}»: задайте шифр журнала-источника");
            var auto = GetStr(c, "auto");
            if (type == "auto" && (auto is null || !AutoKinds.Contains(auto)))
                throw new ApiException(422, $"Колонка «{title}»: тип auto требует auto=row|date|author");
            var formula = (GetStr(c, "formula") ?? "").Trim();
            if (type == "formula")
            {
                if (formula.Length == 0)
                    throw new ApiException(422, $"Колонка «{title}»: задайте формулу");
                var refs = Formula.IdentRe.Matches(formula).Select(m => m.Value).ToHashSet();
                var unknown = refs.Except(keys).Order().ToList();
                if (unknown.Count > 0)
                    throw new ApiException(422,
                        $"Колонка «{title}»: в формуле неизвестные поля: {string.Join(", ", unknown)}");
            }

            result.Add(new FormColumn
            {
                Key = key,
                Title = title,
                Type = type,
                Required = GetBool(c, "required"),
                Unit = NonEmpty((GetStr(c, "unit") ?? "").Trim()),
                Options = options is { Count: > 0 } ? options : null,
                Formula = NonEmpty(formula),
                RefCode = NonEmpty(refCode),
                Auto = auto,
            });
        }
        return result;
    }

    private static string? NonEmpty(string s) => s.Length == 0 ? null : s;

    private static string? GetStr(Dictionary<string, object?> c, string key) => c.TryGetValue(key, out var v)
        ? v switch
        {
            null => null,
            JsonElement { ValueKind: JsonValueKind.String } s => s.GetString(),
            JsonElement j => j.GetRawText(),
            _ => v.ToString(),
        }
        : null;

    private static bool GetBool(Dictionary<string, object?> c, string key) => c.TryGetValue(key, out var v) && v switch
    {
        bool b => b,
        JsonElement { ValueKind: JsonValueKind.True } => true,
        JsonElement { ValueKind: JsonValueKind.False } => false,
        JsonElement { ValueKind: JsonValueKind.Number } n => n.GetDouble() != 0,
        JsonElement { ValueKind: JsonValueKind.String } s => s.GetString()!.Length > 0,
        _ => false,
    };

    private static List<string>? GetList(Dictionary<string, object?> c, string key)
    {
        if (!c.TryGetValue(key, out var v)) return null;
        return v switch
        {
            JsonElement { ValueKind: JsonValueKind.Array } arr =>
                arr.EnumerateArray()
                    .Select(x => x.ValueKind == JsonValueKind.String ? x.GetString() ?? "" : x.GetRawText())
                    .Where(s => s.Trim().Length > 0)
                    .Select(s => s.Trim())
                    .ToList(),
            _ => null,
        };
    }
}

// ---------- DTO ----------

public sealed record TemplateCreate(
    string? Code,
    string? Title,
    List<Dictionary<string, object?>>? Columns,
    [property: JsonPropertyName("is_preset")] bool IsPreset = false);

public sealed record VersionCreate(
    List<Dictionary<string, object?>>? Columns,
    string? Comment = null);
