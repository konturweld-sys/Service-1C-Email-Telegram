namespace OneCIntegrationService.Models;

public sealed class OneCConnectionSettings
{
    public string ConnectionString { get; set; } = string.Empty;
    public string CredentialTargetName { get; set; } = string.Empty;
}

public sealed class MonitorSettings
{
    public int MaskSyncIntervalSeconds { get; set; } = 60;
    public int FallbackPollIntervalSeconds { get; set; } = 15;
    public int LookbackWindowSeconds { get; set; } = 60;
    public long MaxTailBytes { get; set; } = 10 * 1024 * 1024;
    public int MaxLogEntryLength { get; set; } = 2000;
    public string LogFileEncoding { get; set; } = "utf-8";

    public List<string> SupportedDateFormats { get; set; } =
    [
        "yyyy-MM-dd HH:mm:ss",
        "yyyy-MM-dd HH:mm:ss.fff",
        "yyyy-MM-dd'T'HH:mm:ss",
        "dd.MM.yyyy HH:mm:ss",
        "dd.MM.yyyy HH:mm:ss.fff",
        "yyyyMMddHHmmss"
    ];
}
