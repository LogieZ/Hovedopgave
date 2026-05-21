namespace VideoArchiveManager.Configuration;

public sealed class AppSettings
{
    public string ArchiveRootPath { get; set; } = string.Empty;

    public string ConnectionString { get; set; } = string.Empty;

    public string YtDlpPath { get; set; } = "yt-dlp";

    public HashSet<string> VideoExtensions { get; set; } = new(StringComparer.OrdinalIgnoreCase)
    {
        ".mp4", ".mkv", ".avi", ".mov", ".flv", ".wmv", ".webm"
    };

    public int BatchSize { get; set; } = 500;

    public int MetadataEnrichmentBatchSize { get; set; } = 50;

    public bool DownloadEnabled { get; set; } = true;

    public int DownloadBatchSize { get; set; } = 5;

    public int DownloadRetryCount { get; set; } = 3;

    public int DownloadRetryBaseDelayMs { get; set; } = 10000;

    public int DownloadCooldownMs { get; set; } = 20000;

    public string LogPath { get; set; } = "logs/archive-.log";

    public string ChannelUrl { get; set; } = "https://www.youtube.com/@DanmarkCTV/videos";
}