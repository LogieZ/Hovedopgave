using Serilog;
using VideoArchiveManager.Interfaces;
using Microsoft.EntityFrameworkCore;
using VideoArchiveManager.Data;
using VideoArchiveManager.Models;

namespace VideoArchiveManager.Services;

public sealed class DatabaseService : IDatabaseService
{
    private readonly IArchiveDbContextFactory _contextFactory;
    private readonly IVideoTitleMatcher _titleMatcher;

    public DatabaseService(IArchiveDbContextFactory contextFactory, IVideoTitleMatcher titleMatcher)
    {
        _contextFactory = contextFactory;
        _titleMatcher = titleMatcher;
        
        using var context = _contextFactory.CreateDbContext();
        context.Database.EnsureCreated();
    }

    private ArchiveDbContext CreateContext() => _contextFactory.CreateDbContext();

    // Find a video entry by its YouTube ID. Returns null if not found.
    public VideoEntry? FindByYoutubeId(string youtubeId)
    {
        using var context = CreateContext();
        return context.VideoEntries
            .AsNoTracking()
            .FirstOrDefault(e => e.YoutubeId == youtubeId);
    }

    public VideoEntry? FindBestMatchByTitle(string fileName)
    {
        using var context = CreateContext();

        var candidates = context.VideoEntries
            .AsNoTracking()
            .Select(e => new VideoTitleCandidate(e.YoutubeId, e.Title))
            .ToList();

        var matchedYoutubeId = _titleMatcher.FindBestMatchingYoutubeId(fileName, candidates);
        if (!string.IsNullOrWhiteSpace(matchedYoutubeId))
        {
            return context.VideoEntries.FirstOrDefault(v => v.YoutubeId == matchedYoutubeId);
        }

        return null;
    }

    public void AddVideoEntry(VideoEntry entry)
    {
        using var context = CreateContext();
        if (!context.VideoEntries.Any(e => e.YoutubeId == entry.YoutubeId))
        {
            context.VideoEntries.Add(entry);
            context.SaveChanges();
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

        entry.LinkedFilePath = filePath;
        entry.FileSizeBytes = fileSizeBytes;
        entry.Status = status;
        entry.LastVerified = DateTime.UtcNow;
        entry.UpdatedAt = DateTime.UtcNow;

        await context.SaveChangesAsync();
    }

    public IEnumerable<VideoEntry> StreamLinkedButMissingOnDisk()
    {
        var context = CreateContext();
        return context.VideoEntries
            .AsNoTracking()
            .Where(e => e.Status == LinkStatus.Linked && e.LinkedFilePath != null)
            .AsEnumerable();
    }

    // Fetches all video entries that are currently unlinked. This is used by the LinkerService to attempt matching them with files on disk.
    public async Task<List<VideoEntry>> GetAllUnlinkedEntriesAsync()
    {
        using var context = CreateContext();
        return await context.VideoEntries
            .Where(e => e.Status == LinkStatus.Unlinked)
            .ToListAsync();
    }

    // Used by YoutubeService to update the status of a video entry after parsing new data from the JSON lines.
    public void UpdateVideoEntry(VideoEntry entry)
    {
        using var context = CreateContext();
        context.VideoEntries.Update(entry);
        context.SaveChanges();
    }

    // An asynchronous version of the above method, if needed in the future.
    public async Task UpdateVideoEntryAsync(VideoEntry entry)
    {
        using var context = CreateContext();
        context.VideoEntries.Update(entry);
        await context.SaveChangesAsync();
    }

}