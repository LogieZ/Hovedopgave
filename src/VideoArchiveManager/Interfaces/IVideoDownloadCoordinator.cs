using VideoArchiveManager.Models;

namespace VideoArchiveManager.Interfaces;

public interface IVideoDownloadCoordinator
{
    Task<int> DownloadMissingAsync(IReadOnlyCollection<VideoEntry> videos, CancellationToken cancellationToken = default);
}
