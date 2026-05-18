using Serilog;
using VideoArchiveManager.Interfaces;
using VideoArchiveManager.Models;

namespace VideoArchiveManager.Services;

public sealed class MissingFileSynchronizationService
{
    private readonly IDatabaseService _databaseService;
    private readonly IFileSystem _fileSystem;
    private readonly IYoutubeService _youtubeService;

    public MissingFileSynchronizationService(
        IDatabaseService databaseService,
        IFileSystem fileSystem,
        IYoutubeService youtubeService)
    {
        _databaseService = databaseService;
        _fileSystem = fileSystem;
        _youtubeService = youtubeService;
    }

    public async Task<int> SyncMissingLinkedFilesAsync(CancellationToken cancellationToken = default)
    {
        var triggeredDownloads = 0;

        foreach (var video in _databaseService.StreamLinkedButMissingOnDisk())
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (string.IsNullOrWhiteSpace(video.LinkedFilePath))
            {
                continue;
            }

            if (_fileSystem.FileExists(video.LinkedFilePath))
            {
                continue;
            }

            video.Status = LinkStatus.Downloading;
            video.UpdatedAt = DateTime.UtcNow;
            _databaseService.UpdateVideoEntry(video);

            var destinationFolder = Path.GetDirectoryName(video.LinkedFilePath);
            if (string.IsNullOrWhiteSpace(destinationFolder))
            {
                video.Status = LinkStatus.DownloadFailed;
                video.UpdatedAt = DateTime.UtcNow;
                _databaseService.UpdateVideoEntry(video);
                continue;
            }

            var success = await _youtubeService
                .DownloadVideoAsync(video, destinationFolder)
                .ConfigureAwait(false);

            if (!success)
            {
                Log.Warning("Download failed for missing file sync: {YoutubeId}", video.YoutubeId);
                video.Status = LinkStatus.DownloadFailed;
                video.UpdatedAt = DateTime.UtcNow;
                _databaseService.UpdateVideoEntry(video);
                continue;
            }

            triggeredDownloads++;
        }

        return triggeredDownloads;
    }
}
