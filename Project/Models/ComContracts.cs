using System.Text.Json.Serialization;

namespace OneCIntegrationService.Models;

public sealed class GetMasksResponse
{
    [JsonPropertyName("success")]
    public bool Success { get; set; }

    // null — некорректный ответ, [] — корректный ответ с отсутствием активных масок.
    [JsonPropertyName("masks")]
    public List<LogMaskDto>? Masks { get; set; }

    [JsonPropertyName("error")]
    public string? Error { get; set; }
}

public sealed class LogMaskDto
{
    [JsonPropertyName("maskId")]
    public string MaskId { get; set; } = string.Empty;

    [JsonPropertyName("maskName")]
    public string MaskName { get; set; } = string.Empty;

    [JsonPropertyName("filePath")]
    public string FilePath { get; set; } = string.Empty;

    [JsonPropertyName("timeRegex")]
    public string TimeRegex { get; set; } = string.Empty;

    [JsonPropertyName("searchType")]
    public string SearchType { get; set; } = "ПоследовательностьСлов";

    [JsonPropertyName("searchPattern")]
    public string SearchPattern { get; set; } = string.Empty;

    [JsonPropertyName("excludeWords")]
    public string ExcludeWords { get; set; } = string.Empty;

    [JsonPropertyName("caseSensitive")]
    public bool CaseSensitive { get; set; }

    [JsonPropertyName("messageTemplate")]
    public string MessageTemplate { get; set; } = string.Empty;
}

public sealed class RegisterEventRequest
{
    [JsonPropertyName("maskId")]
    public string MaskId { get; set; } = string.Empty;

    [JsonPropertyName("eventTime")]
    public string EventTime { get; set; } = string.Empty;

    [JsonPropertyName("eventText")]
    public string EventText { get; set; } = string.Empty;
}

public sealed class RegisterEventResponse
{
    [JsonPropertyName("success")]
    public bool Success { get; set; }

    [JsonPropertyName("error")]
    public string? Error { get; set; }
}
