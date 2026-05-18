using Serilog;
using VideoArchiveManager.Interfaces;
using VideoArchiveManager.Configuration;
using Microsoft.EntityFrameworkCore;
using VideoArchiveManager.Data;
using System.Text.RegularExpressions;
using VideoArchiveManager.Models;

namespace VideoArchiveManager.Services;

public sealed class DatabaseService : IDatabaseService
{
    private readonly AppSettings _settings;
    private List<(string YoutubeId, string Title)>? _candidateCache;

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

    public VideoEntry? FindBestMatchByTitle(string fileName)
    {
        using var context = CreateContext();
        var fileNameOnly = Path.GetFileNameWithoutExtension(fileName);
        
        // Remove leading numbers and dashes to focus on the core title (e.g., "2023-01-01 - My Video Title" -> "My Video Title")
        var cleanTitleFragment = Regex.Replace(fileNameOnly, @"^[0-9-]+", "").Trim();
        
        // Fetch all candidates (we do this once per batch for efficiency)
        if (_candidateCache == null)
        {
            using var cacheContext = CreateContext();
            _candidateCache = cacheContext.VideoEntries
                .AsNoTracking()
                .Select(e => new ValueTuple<string, string>(e.YoutubeId, e.Title))
                .ToList();
        }

        var candidates = _candidateCache;

        // 1. Simple substring match - check if the cleaned title fragment is contained in any video title or vice versa (case-insensitive)
        var simpleMatch = candidates.FirstOrDefault(v => 
            v.Title.Contains(cleanTitleFragment, StringComparison.OrdinalIgnoreCase) || 
            cleanTitleFragment.Contains(v.Title, StringComparison.OrdinalIgnoreCase));

        if (!string.IsNullOrWhiteSpace(simpleMatch.YoutubeId))
        {
            return context.VideoEntries.FirstOrDefault(v => v.YoutubeId == simpleMatch.YoutubeId);
        }

        // 2. Tokenization - split the title into words and find the best match based on word overlap
        var fileWords = cleanTitleFragment.ToLower()
            .Split(new[] { ' ', '-', '_' }, StringSplitOptions.RemoveEmptyEntries)
            .Where(w => w.Length >= 2).ToList();

        var bestTokenMatch = candidates.Select(v => new { 
            Entry = v, 
            Score = v.Title.ToLower().Split(' ').Intersect(fileWords).Count() 
        })
        .Where(x => x.Score >= 2) 
        .OrderByDescending(x => x.Score)
        .FirstOrDefault();

        if (bestTokenMatch != null)
        {
            return context.VideoEntries.FirstOrDefault(v => v.YoutubeId == bestTokenMatch.Entry.YoutubeId);
        }

        var fuzzyMatch = candidates
            .Select(v => new { Entry = v, Distance = CalculateLevenshteinDistance(cleanTitleFragment.ToLower(), v.Title.ToLower()) })
            .OrderBy(x => x.Distance)
            .FirstOrDefault();

        // We only accept the match if the titles are very similar (e.g., max 5 characters difference)
        if (fuzzyMatch != null && fuzzyMatch.Distance < 8) 
        {
            return context.VideoEntries.FirstOrDefault(v => v.YoutubeId == fuzzyMatch.Entry.YoutubeId);
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

        entry.LinkedFilePath = filePath;
        entry.FileSizeBytes = fileSizeBytes;
        entry.Status = status;
        entry.LastVerified = DateTime.UtcNow;
        entry.UpdatedAt = DateTime.UtcNow;

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

    // Fetches all video entries that are currently unlinked. This is used by the LinkerService to attempt matching them with files on disk.
    public async Task<List<VideoEntry>> GetAllUnlinkedEntriesAsync()
    {
        using var context = CreateContext();
        return await context.VideoEntries
            .Where(e => e.Status == LinkStatus.Unlinked)
            .ToListAsync();
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

    // Used by YoutubeService to update the status of a video entry after parsing new data from the JSON lines.
    public void UpdateVideoEntry(VideoEntry entry)
    {
        using var context = CreateContext();
        context.VideoEntries.Update(entry);
        context.SaveChanges();
        _candidateCache = null;
    }

    // An asynchronous version of the above method, if needed in the future.
    public async Task UpdateVideoEntryAsync(VideoEntry entry)
    {
        using var context = CreateContext();
        context.VideoEntries.Update(entry);
        await context.SaveChangesAsync();
    }

    // Calculates the Levenshtein distance between two strings, which is a measure of how many single-character edits are needed to change one string into the other. This is used for fuzzy matching of titles.
    private int CalculateLevenshteinDistance(string source, string target)
    {
        if (string.IsNullOrEmpty(source)) return target.Length;
        if (string.IsNullOrEmpty(target)) return source.Length;

        var distance = new int[source.Length + 1, target.Length + 1];

        for (int i = 0; i <= source.Length; i++) distance[i, 0] = i;
        for (int j = 0; j <= target.Length; j++) distance[0, j] = j;

        for (int i = 1; i <= source.Length; i++)
        {
            for (int j = 1; j <= target.Length; j++)
            {
                int cost = (target[j - 1] == source[i - 1]) ? 0 : 1;
                distance[i, j] = Math.Min(Math.Min(distance[i - 1, j] + 1, distance[i, j - 1] + 1), distance[i - 1, j - 1] + cost);
            }
        }
        return distance[source.Length, target.Length];
    }
}