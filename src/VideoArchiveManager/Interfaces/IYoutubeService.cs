using VideoArchiveManager.Models;

namespace VideoArchiveManager.Interfaces;

public interface IYoutubeService
{
    Task ImportChannelVideosAsync(string channelUrl);
    Task<int> EnrichMissingMetadataAsync(int limit, CancellationToken cancellationToken = default);
    Task<bool> DownloadVideoAsync(
        VideoEntry entry,
        string destinationFolder,
        CancellationToken cancellationToken = default);
}