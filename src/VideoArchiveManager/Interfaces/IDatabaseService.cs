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
    void UpdateVideoEntry(VideoEntry entry);
}