using System.Globalization;
using System.Text.Json;
using EJL.Data;
using EJL.Util;

namespace EJL.Domain;

/// <summary>Нормализация и валидация данных записи по колонкам шаблона —
/// порт apply_columns из records-svc (ГОСТ 17025: обязательные поля, автополя, формулы).</summary>
public static class Columns
{
    public static Dictionary<string, object?> Apply(
        List<FormColumn> columns, Dictionary<string, object?>? raw,
        EjlDbContext db, CurrentUser user, int rowNo,
        Dictionary<string, object?>? oldData = null)
    {
        raw ??= new();
        var data = new Dictionary<string, object?>();
        var errors = new List<string>();

        // 1) автополя (№ строки, дата, автор); при исправлении — как было
        foreach (var c in columns.Where(c => c.Type == "auto"))
        {
            if (oldData is not null && oldData.TryGetValue(c.Key, out var old))
            {
                data[c.Key] = old;
                continue;
            }
            data[c.Key] = c.Auto switch
            {
                "row" => rowNo,
                "date" => DateTime.Today.ToString("yyyy-MM-dd"),
                "author" => user.Name ?? "",
                _ => null,
            };
        }

        // 2) обычные колонки: типизация + валидация
        foreach (var c in columns)
        {
            if (c.Type is "auto" or "formula") continue;
            var v = raw.TryGetValue(c.Key, out var rv) ? rv : null;

            switch (c.Type)
            {
                case "number":
                    if (IsEmpty(v)) v = null;
                    else if (ToDouble(v) is { } num) v = num;
                    else
                    {
                        errors.Add($"«{c.Title}»: ожидается число");
                        v = null;
                    }
                    break;

                case "date":
                    if (IsEmpty(v)) v = null;
                    else
                    {
                        var s = AsString(v) ?? "";
                        var t = s.Length > 10 ? s[..10] : s;
                        if (DateTime.TryParseExact(t, "yyyy-MM-dd", CultureInfo.InvariantCulture,
                                DateTimeStyles.None, out var d))
                            v = d.ToString("yyyy-MM-dd");
                        else
                        {
                            errors.Add($"«{c.Title}»: дата в формате ГГГГ-ММ-ДД");
                            v = null;
                        }
                    }
                    break;

                case "bool":
                    v = IsEmpty(v) ? null : Truthy(v);
                    break;

                case "enum":
                    if (IsEmpty(v)) v = null;
                    else
                    {
                        var s = AsString(v);
                        if (s is not null && (c.Options ?? []).Contains(s)) v = s;
                        else
                        {
                            errors.Add($"«{c.Title}»: выберите значение из списка");
                            v = null;
                        }
                    }
                    break;

                case "ref_journal":
                case "ref_material":
                    if (IsEmpty(v)) v = null;
                    else if (ToInt(v) is not { } refId)
                    {
                        errors.Add($"«{c.Title}»: неверная ссылка");
                        v = null;
                    }
                    else
                    {
                        v = (int)refId;
                        if (c.Type == "ref_journal")
                        {
                            var target = db.Records.Find(refId);
                            if (target is null)
                                errors.Add($"«{c.Title}»: запись №{refId} не найдена");
                            else
                            {
                                var tj = db.Journals.Find(target.JournalId);
                                if (tj is null || tj.Code != c.RefCode)
                                    errors.Add($"«{c.Title}»: запись не из журнала {c.RefCode}");
                            }
                        }
                    }
                    break;

                default: // text
                    v = IsEmpty(v) ? null : (AsString(v)?.Trim() ?? null);
                    if (v is string sv && sv.Length == 0) v = null;
                    break;
            }

            if (c.Required && (v is null || v is ""))
                errors.Add($"«{c.Title}»: обязательное поле");
            data[c.Key] = v;
        }

        if (errors.Count > 0)
            throw new ApiException(422, string.Join("; ", errors));

        // 3) расчётные колонки (до 5 проходов — формулы могут ссылаться друг на друга)
        for (var pass = 0; pass < 5; pass++)
            foreach (var c in columns.Where(c => c.Type == "formula"))
                data[c.Key] = Formula.Eval(c.Formula, data);

        return data;
    }

    /// <summary>Пустое значение: null или пустая строка (как v in (None, \"\") в Python).</summary>
    private static bool IsEmpty(object? v) => v switch
    {
        null => true,
        "" => true,
        JsonElement { ValueKind: JsonValueKind.Null } => true,
        JsonElement { ValueKind: JsonValueKind.String } s => string.IsNullOrEmpty(s.GetString()),
        _ => false,
    };

    /// <summary>Строковое представление значения (для enum/text/date).</summary>
    private static string? AsString(object? v) => v switch
    {
        null => null,
        string s => s,
        JsonElement { ValueKind: JsonValueKind.String } s => s.GetString(),
        JsonElement j => j.GetRawText(),
        bool b => b ? "True" : "False", // str(True) в Python
        double or int or long => Convert.ToString(v, CultureInfo.InvariantCulture),
        _ => v.ToString(),
    };

    /// <summary>float(str(v).replace(\",\", \".\")) — число из JSON или строки.</summary>
    private static double? ToDouble(object? v) => v switch
    {
        JsonElement { ValueKind: JsonValueKind.Number } n => n.GetDouble(),
        JsonElement { ValueKind: JsonValueKind.String } s => ParseNum(s.GetString()),
        string s => ParseNum(s),
        double d => d,
        int i => i,
        long l => l,
        _ => null, // bool и прочее — не число
    };

    private static double? ParseNum(string? s)
    {
        if (string.IsNullOrEmpty(s)) return null;
        return double.TryParse(s.Replace(",", "."), NumberStyles.Float,
            CultureInfo.InvariantCulture, out var d) ? d : null;
    }

    /// <summary>int(v): обрезание дробной части, как в Python (int(1.5) = 1).</summary>
    private static long? ToInt(object? v) => v switch
    {
        JsonElement { ValueKind: JsonValueKind.Number } n => (long)Math.Truncate(n.GetDouble()),
        JsonElement { ValueKind: JsonValueKind.String } s when long.TryParse(s.GetString(), out var li) => li,
        double d => (long)Math.Truncate(d),
        int i => i,
        long l => l,
        _ => null,
    };

    /// <summary>bool(v) по правилам Python: непустая строка — истина (даже «false»), 0 — ложь.</summary>
    private static object? Truthy(object? v) => v switch
    {
        bool b => b,
        JsonElement { ValueKind: JsonValueKind.True } => true,
        JsonElement { ValueKind: JsonValueKind.False } => false,
        JsonElement { ValueKind: JsonValueKind.Number } n => n.GetDouble() != 0,
        JsonElement { ValueKind: JsonValueKind.String } s => s.GetString()!.Length > 0,
        string s => s.Length > 0,
        double d => d != 0,
        int i => i != 0,
        long l => l != 0,
        _ => true,
    };
}
