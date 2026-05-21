using Serilog;
using VideoArchiveManager.Interfaces;
using VideoArchiveManager.Configuration;
using Microsoft.EntityFrameworkCore;
using VideoArchiveManager.Data;
using VideoArchiveManager.Models;

namespace VideoArchiveManager.Services;

public sealed class DatabaseService : IDatabaseService
{
    private readonly AppSettings _settings;
    private List<VideoMatchCandidate>? _candidateCache;

    public DatabaseService(AppSettings settings)
    {
        _settings = settings;
        
        using var context = new ArchiveDbContext(_settings.ConnectionString);
        context.Database.EnsureCreated();
    }

    private ArchiveDbContext CreateContext() => new ArchiveDbContext(_settings.ConnectionString);

    // Find a video entry by its YouTube ID. Returns null if not found.
    public VideoEntry? FindByYoutubeId(string youtubeId)
    {
        using var context = CreateContext();
        return context.VideoEntries
            .AsNoTracking()
            .FirstOrDefault(e => e.YoutubeId == youtubeId);
    }

    public VideoEntry? FindBestMatchByTitle(string fileName, long fileDurationSeconds = 0, string filePath = "")
    {
        using var context = CreateContext();

        // Fetch all candidates (we do this once per batch for efficiency)
        if (_candidateCache == null)
        {
            using var cacheContext = CreateContext();
            _candidateCache = cacheContext.VideoEntries
                .AsNoTracking()
                .Where(e => e.Status == LinkStatus.Unlinked || e.Status == LinkStatus.Missing || e.Status == LinkStatus.Linked)
                .Select(e => new { e.YoutubeId, e.Title, e.DurationSeconds, e.UploadedDate })
                .ToList()
                .Select(e => new VideoMatchCandidate(
                    e.YoutubeId,
                    e.Title,
                    e.DurationSeconds,
                    VideoTitleMatcher.ExtractDateFromTitle(e.Title),
                    e.UploadedDate,
                    VideoTitleMatcher.ExtractSignificantWords(e.Title)))
                .ToList();
        }

        var matchedYoutubeId = VideoTitleMatcher.FindBestMatchYoutubeId(
            _candidateCache,
            fileName,
            fileDurationSeconds,
            filePath);

        if (!string.IsNullOrWhiteSpace(matchedYoutubeId))
        {
            return context.VideoEntries.FirstOrDefault(v => v.YoutubeId == matchedYoutubeId);
        }

        return null;
    }

    public void AddVideoEntry(VideoEntry entry)
    {
        using var context = CreateContext();
        var existing = context.VideoEntries.FirstOrDefault(e => e.YoutubeId == entry.YoutubeId);
        if (existing == null)
        {
            context.VideoEntries.Add(entry);
            context.SaveChanges();
            _candidateCache = null;
            return;
        }

        bool changed = false;

        if (string.IsNullOrWhiteSpace(existing.Title) && !string.IsNullOrWhiteSpace(entry.Title))
        {
            existing.Title = entry.Title;
            changed = true;
        }

        if (existing.DurationSeconds <= 0 && entry.DurationSeconds > 0)
        {
            existing.DurationSeconds = entry.DurationSeconds;
            changed = true;
        }

        if (existing.UploadedDate == null && entry.UploadedDate != null)
        {
            existing.UploadedDate = entry.UploadedDate;
            changed = true;
        }

        if (changed)
        {
            existing.UpdatedAt = DateTime.UtcNow;
            context.SaveChanges();
            _candidateCache = null;
        }
    }

    public async Task UpdateLink(string youtubeId, string? filePath, long? fileSizeBytes, LinkStatus status)
    {
        using var context = CreateContext();
        var entry = await context.VideoEntries
            .FirstOrDefaultAsync(e => e.YoutubeId == youtubeId);

        if (entry == null)
        {
            Log.Warning("No database entry found for YouTube ID: {YoutubeId}", youtubeId);
            return;
        }

        // Use domain methods to enforce state transitions
        switch (status)
        {
            case LinkStatus.Linked:
                entry.MarkAsLinked(filePath, fileSizeBytes);
                break;
            case LinkStatus.Missing:
                entry.MarkAsMissing();
                break;
            case LinkStatus.DownloadFailed:
                entry.MarkAsDownloadFailed();
                break;
            case LinkStatus.Downloading:
                entry.MarkAsDownloading();
                break;
            default:
                Log.Warning("Unexpected status transition to {Status} for {YoutubeId}", status, youtubeId);
                break;
        }

        await context.SaveChangesAsync();
    }

    public IEnumerable<VideoEntry> StreamLinkedButMissingOnDisk()
    {
        using var context = CreateContext();
        return context.VideoEntries
            .AsNoTracking()
            .Where(e => e.Status == LinkStatus.Linked && e.LinkedFilePath != null)
            .OrderByDescending(e => e.LastVerified)
            .ThenByDescending(e => e.UpdatedAt)
            .ToList()
            .AsEnumerable();
    }

    public async Task<List<VideoEntry>> GetEntriesMissingUploadedDateAsync(int limit)
    {
        using var context = CreateContext();

        return await context.VideoEntries
            .AsNoTracking()
            .Where(e => e.UploadedDate == null)
            .OrderByDescending(e => e.Status == LinkStatus.Linked)
            .ThenBy(e => e.UpdatedAt)
            .Take(limit)
            .ToListAsync();
    }

    public async Task<int> CountEntriesMissingUploadedDateAsync()
    {
        using var context = CreateContext();

        return await context.VideoEntries
            .AsNoTracking()
            .Where(e => e.UploadedDate == null)
            .CountAsync();
    }

    public async Task<List<VideoEntry>> GetDownloadCandidatesAsync(int limit)
    {
        using var context = CreateContext();

        return await context.VideoEntries
            .AsNoTracking()
            .Where(e => e.Status == LinkStatus.Unlinked || e.Status == LinkStatus.Missing)
            .Where(e => e.Status != LinkStatus.DownloadFailed)
            .OrderByDescending(e => e.UploadedDate)
            .ThenBy(e => e.UpdatedAt)
            .Take(limit)
            .ToListAsync();
    }

    public async Task<int> CountDownloadCandidatesAsync()
    {
        using var context = CreateContext();

        return await context.VideoEntries
            .AsNoTracking()
            .Where(e => e.Status == LinkStatus.Unlinked || e.Status == LinkStatus.Missing)
            .Where(e => e.Status != LinkStatus.DownloadFailed)
            .CountAsync();
    }

    // Resets any entries stuck in Downloading state back to Unlinked.
    // This covers the case where the program crashed mid-download and left a video permanently in Downloading.
    public async Task<int> ResetStuckDownloadsAsync()
    {
        using var context = CreateContext();

        var stuck = await context.VideoEntries
            .Where(e => e.Status == LinkStatus.Downloading)
            .ToListAsync();

        foreach (var entry in stuck)
        {
            entry.ResetFromStuckDownload();
        }

        if (stuck.Count > 0)
        {
            await context.SaveChangesAsync();
            _candidateCache = null;
        }

        return stuck.Count;
    }

    // Used by YoutubeService to update the status of a video entry after parsing new data from the JSON lines.
    public async Task UpdateVideoEntryAsync(VideoEntry entry)
    {
        using var context = CreateContext();
        context.VideoEntries.Update(entry);
        await context.SaveChangesAsync();
        _candidateCache = null;
    }

}