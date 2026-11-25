using System.Text.Json.Serialization;

namespace EJL.Domain;

/// <summary>Колонка шаблона формы (JSON в БД и API — snake_case, как у прежнего forms-svc).</summary>
public class FormColumn
{
    [JsonPropertyName("key")] public string Key { get; set; } = "";
    [JsonPropertyName("title")] public string Title { get; set; } = "";
    [JsonPropertyName("type")] public string Type { get; set; } = "";
    [JsonPropertyName("required")] public bool Required { get; set; }
    [JsonPropertyName("unit")] public string? Unit { get; set; }
    [JsonPropertyName("options")] public List<string>? Options { get; set; }
    [JsonPropertyName("formula")] public string? Formula { get; set; }
    [JsonPropertyName("ref_code")] public string? RefCode { get; set; }
    [JsonPropertyName("auto")] public string? Auto { get; set; } // row | date | author
}
