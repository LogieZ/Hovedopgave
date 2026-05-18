using Serilog;
using VideoArchiveManager.Interfaces;
using VideoArchiveManager.Models;

namespace VideoArchiveManager.Services;

public sealed class VideoDownloadCoordinator : IVideoDownloadCoordinator
{
    private readonly IYoutubeService _youtubeService;
    private readonly IDownloadRetryPolicy _retryPolicy;
    private readonly IAppSettings _settings;

    public VideoDownloadCoordinator(
        IYoutubeService youtubeService,
        IDownloadRetryPolicy retryPolicy,
        IAppSettings settings)
    {
        _youtubeService = youtubeService;
        _retryPolicy = retryPolicy;
        _settings = settings;
    }

    public async Task<int> DownloadMissingAsync(IReadOnlyCollection<VideoEntry> videos, CancellationToken cancellationToken = default)
    {
        var downloadedCount = 0;

        foreach (var video in videos)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var success = await _retryPolicy.ExecuteAsync(
                async (attempt, token) =>
                {
                    if (attempt > 1)
                    {
                        Log.Warning(
                            "Retry attempt {Attempt} of {MaxRetries} for video {Title}",
                            attempt,
                            _settings.MaxDownloadRetries,
                            video.Title);
                    }

                    return await _youtubeService
                        .DownloadVideoAsync(video, _settings.ArchiveRootPath)
                        .ConfigureAwait(false);
                },
                cancellationToken).ConfigureAwait(false);

            if (success)
            {
                downloadedCount++;
                Log.Information("Successfully downloaded video: {Title}", video.Title);
                await Task.Delay(TimeSpan.FromSeconds(_settings.InterDownloadDelaySeconds), cancellationToken).ConfigureAwait(false);
            }
            else
            {
                Log.Error("All retries failed. Skipping video: {Title}", video.Title);
            }
        }

        return downloadedCount;
    }
}
