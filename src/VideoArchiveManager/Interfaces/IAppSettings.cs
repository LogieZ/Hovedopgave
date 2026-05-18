namespace VideoArchiveManager.Interfaces;

public interface IAppSettings
{
    string ArchiveRootPath { get; }
    string ConnectionString { get; }
    string YtDlpPath { get; }
    string DownloadOutputPath { get; }
    HashSet<string> VideoExtensions { get; }
    int MaxConcurrency { get; }
    int BatchSize { get; }
    bool DownloadMissing { get; }
    string LogPath { get; }
    string ChannelUrl { get; }
    int MaxDownloadsPerRun { get; }
    int MaxDownloadRetries { get; }
    int RetryBaseDelaySeconds { get; }
    int InterDownloadDelaySeconds { get; }
}
