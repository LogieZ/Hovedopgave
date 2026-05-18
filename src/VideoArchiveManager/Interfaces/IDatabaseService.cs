using VideoArchiveManager.Models;

namespace VideoArchiveManager.Interfaces;

public interface IDatabaseService
{
    VideoEntry? FindByYoutubeId(string youtubeId);
    VideoEntry? FindBestMatchByTitle(string title);
    void AddVideoEntry(VideoEntry entry);
    Task UpdateLink(string youtubeId, string? filePath, long? fileSizeBytes, LinkStatus status);
    IEnumerable<VideoEntry> StreamLinkedButMissingOnDisk();
    Task<List<VideoEntry>> GetAllUnlinkedEntriesAsync();
    Task<List<VideoEntry>> GetDownloadCandidatesAsync(int limit);
    Task<int> CountDownloadCandidatesAsync();
    void UpdateVideoEntry(VideoEntry entry);
}