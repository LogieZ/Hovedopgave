namespace VideoArchiveManager.Configuration;

public sealed class AppSettings
{
    public string ArchiveRootPath { get; set; } = string.Empty;

    public string ConnectionString { get; set; } = "Host=localhost;Database=video_archive;Username=postgres;Password=1234";

    public string YtDlpPath { get; set; } = "yt-dlp";

    public string DownloadOutputPath { get; set; } = "downloads";

    public HashSet<string> VideoExtensions { get; set; } = new(StringComparer.OrdinalIgnoreCase)
    {
        ".mp4", ".mkv", ".avi", ".mov", ".flv", ".wmv", ".webm"
    };

    public int MaxConcurrency { get; set; } = 2;

    public int BatchSize { get; set; } = 500;

    public bool DownloadMissing { get; set; } = false;

    public string LogPath { get; set; } = "logs/archive-.log";
}