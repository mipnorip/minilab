using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace EJL.Domain;

/// <summary>Безопасный вычислитель формул вида «a * 1000 / b» — порт safe_eval из records-svc.
/// None-операнды, деление на ноль и любые ошибки разбора дают null.</summary>
public static class Formula
{
    private static readonly Regex TokenRe =
        new(@"\s*(?:(\d+(?:\.\d+)?)|([A-Za-z_][A-Za-z0-9_]*)|(.))", RegexOptions.Compiled);

    public static readonly Regex IdentRe = new(@"[A-Za-z_][A-Za-z0-9_]*", RegexOptions.Compiled);

    private static readonly HashSet<char> Ops = ['+', '-', '*', '/', '(', ')'];

    private sealed record Tok(string Kind, string Text, double Num);

    private static List<Tok>? Tokenize(string expr)
    {
        var tokens = new List<Tok>();
        var pos = 0;
        while (pos < expr.Length)
        {
            var m = TokenRe.Match(expr, pos);
            if (!m.Success) return null;
            pos = m.Index + m.Length;
            if (m.Groups[1].Success)
                tokens.Add(new Tok("num", m.Groups[1].Value,
                    double.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture)));
            else if (m.Groups[2].Success)
                tokens.Add(new Tok("id", m.Groups[2].Value, 0));
            else
            {
                var ch = m.Groups[3].Value[0];
                if (!Ops.Contains(ch)) return null;
                tokens.Add(new Tok("op", m.Groups[3].Value, 0));
            }
        }
        return tokens;
    }

    public static double? Eval(string? expr, Dictionary<string, object?> values)
    {
        var tokens = Tokenize(expr ?? "");
        if (tokens is null || tokens.Count == 0) return null;
        var idx = 0;

        Tok? Peek() => idx < tokens.Count ? tokens[idx] : null;

        double? ParseExpr()
        {
            var val = ParseTerm();
            while (Peek() is { Kind: "op" } t && (t.Text is "+" or "-"))
            {
                idx++;
                var rhs = ParseTerm();
                if (val is null || rhs is null) val = null;
                else if (t.Text == "+") val = val + rhs;
                else val = val - rhs;
            }
            return val;
        }

        double? ParseTerm()
        {
            var val = ParseFactor();
            while (Peek() is { Kind: "op" } t && (t.Text is "*" or "/"))
            {
                idx++;
                var rhs = ParseFactor();
                if (val is null || rhs is null) val = null;
                else if (t.Text == "*") val = val * rhs;
                else if (rhs == 0) val = null;
                else val = val / rhs;
            }
            return val;
        }

        double? ParseFactor()
        {
            var tok = Peek();
            if (tok is null) return null;
            if (tok.Kind == "num") { idx++; return tok.Num; }
            if (tok is { Kind: "op", Text: "-" })
            {
                idx++;
                var v = ParseFactor();
                return v is null ? null : -v;
            }
            if (tok is { Kind: "op", Text: "(" })
            {
                idx++;
                var v = ParseExpr();
                if (Peek() is { Kind: "op", Text: ")" }) { idx++; return v; }
                return null;
            }
            if (tok.Kind == "id")
            {
                idx++;
                return ToNumber(values.TryGetValue(tok.Text, out var val) ? val : null);
            }
            return null;
        }

        var result = ParseExpr();
        if (idx != tokens.Count || result is null) return null;
        return Math.Round(result.Value, 6);
    }

    /// <summary>Число или null: как в Python — bool и строки не считаются числами.</summary>
    public static double? ToNumber(object? v) => v switch
    {
        null => null,
        bool => null,
        double d => d,
        int i => i,
        long l => l,
        JsonElement { ValueKind: JsonValueKind.Number } n => n.GetDouble(),
        _ => null,
    };
}
