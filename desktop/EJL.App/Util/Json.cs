using System.Text.Json;
using System.Text.Json.Serialization;

namespace EJL.Util;

/// <summary>Общие настройки JSON: имена полей как есть (snake_case задаём вручную),
/// числа с плавающей точкой и даты — в формате, совместимом с прежним Python-бэкендом.</summary>
public static class Json
{
    /// <summary>Готовые настройки для ручной сериализации (ошибки и т.п.).</summary>
    public static readonly JsonSerializerOptions Opts = Create();

    public static void Configure(JsonSerializerOptions o)
    {
        o.PropertyNamingPolicy = null;          // имена свойств не переименоваются
        o.PropertyNameCaseInsensitive = true;   // DTO-биндинг login/Login
        o.DefaultIgnoreCondition = JsonIgnoreCondition.Never;
        o.Converters.Add(new PythonFloatConverter());
        o.Converters.Add(new UtcDateTimeConverter());
    }

    private static JsonSerializerOptions Create()
    {
        var o = new JsonSerializerOptions();
        Configure(o);
        return o;
    }
}

/// <summary>double → JSON так, как это делал Python: 5.0 остаётся «5.0», а не «5».</summary>
public sealed class PythonFloatConverter : JsonConverter<double>
{
    public override double Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        => reader.GetDouble();

    public override void Write(Utf8JsonWriter writer, double value, JsonSerializerOptions options)
    {
        if (!double.IsFinite(value)) { writer.WriteNullValue(); return; }
        var s = value.ToString("R", System.Globalization.CultureInfo.InvariantCulture);
        if (!s.Contains('.') && !s.Contains('E') && !s.Contains('e')) s += ".0";
        writer.WriteRawValue(s);
    }
}

/// <summary>DateTime → ISO-8601 UTC («2026-11-25T12:34:56.123456Z»), независимо от Kind.</summary>
public sealed class UtcDateTimeConverter : JsonConverter<DateTime>
{
    public override DateTime Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        var v = reader.GetDateTime();
        return v.Kind switch
        {
            DateTimeKind.Utc => v,
            DateTimeKind.Local => v.ToUniversalTime(),
            _ => DateTime.SpecifyKind(v, DateTimeKind.Utc),
        };
    }

    public override void Write(Utf8JsonWriter writer, DateTime value, JsonSerializerOptions options)
    {
        var utc = value.Kind == DateTimeKind.Utc ? value : DateTime.SpecifyKind(value, DateTimeKind.Utc);
        writer.WriteStringValue(
            utc.ToString("yyyy-MM-dd'T'HH:mm:ss.ffffff'Z'", System.Globalization.CultureInfo.InvariantCulture));
    }
}
