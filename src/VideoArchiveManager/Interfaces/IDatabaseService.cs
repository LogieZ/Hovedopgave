using VideoArchiveManager.Models;

namespace VideoArchiveManager.Interfaces;

public interface IDatabaseService
{
    VideoEntry? FindBestMatchByTitle(string title, long fileDurationSeconds = 0, string filePath = "");
    void AddVideoEntry(VideoEntry entry);
    Task UpdateLink(string youtubeId, string? filePath, long? fileSizeBytes, LinkStatus status);
    IEnumerable<VideoEntry> StreamLinkedButMissingOnDisk();

    Task<List<VideoEntry>> GetEntriesMissingUploadedDateAsync(int limit);
    Task<int> CountEntriesMissingUploadedDateAsync();
    Task<List<VideoEntry>> GetDownloadCandidatesAsync(int limit);
    Task<int> CountDownloadCandidatesAsync();
    Task<int> ResetStuckDownloadsAsync();
    Task UpdateVideoEntryAsync(VideoEntry entry);
}