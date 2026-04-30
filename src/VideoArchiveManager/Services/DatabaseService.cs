using Serilog;
using VideoArchiveManager.Models;
using VideoArchiveManager.Configuration;
using Microsoft.EntityFrameworkCore;
using VideoArchiveManager.Data;

namespace VideoArchiveManager.Services;

public sealed class DatabaseService
{
    private readonly AppSettings _settings;

    public DatabaseService(AppSettings settings)
    {
        _settings = settings;
        
        using var context = new ArchiveDbContext(_settings.ConnectionString);
        context.Database.EnsureCreated();
    }

    private ArchiveDbContext CreateContext() => new ArchiveDbContext(_settings.ConnectionString);

    public VideoEntry? FindByYoutubeId(string youtubeId)
    {
        using var context = CreateContext();
        return context.VideoEntries
            .AsNoTracking()
            .FirstOrDefault(e => e.YoutubeId == youtubeId);
    }

    public async Task UpdateLink(string youtubeId, string? filePath, long? fileSizeBytes, LinkStatus status)
    {
        using var context = CreateContext();
        var entry = await context.VideoEntries
            .FirstOrDefaultAsync(e => e.YoutubeId == youtubeId);

        if (entry == null)
        {
            Log.Warning("Ingen database-post fundet for YouTube ID: {YoutubeId}", youtubeId);
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
}