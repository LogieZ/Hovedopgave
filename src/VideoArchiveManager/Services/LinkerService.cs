using Serilog;
using VideoArchiveManager.Models;
using VideoArchiveManager.Configuration;

namespace VideoArchiveManager.Services;

public sealed class LinkerService
{
    private readonly DatabaseService _db;
    private readonly AppSettings _settings;

    public LinkerService(DatabaseService db, AppSettings settings)
    {
        _db = db;
        _settings = settings;
    }

    public async Task<LinkReport> LinkAsync(
        ArchiveScanner scanner,
        CancellationToken cancellationToken = default)
    {
        var report = new LinkReport();
        var batch = new List<FileRecord>(_settings.BatchSize);

        foreach (var file in scanner.Scan(cancellationToken))
        {
            batch.Add(file);
            if (batch.Count >= _settings.BatchSize)
            {
                await ProcessBatchAsync(batch, report, cancellationToken).ConfigureAwait(false);
                batch.Clear();
            }
        }

        if (batch.Count > 0)
        {
            await ProcessBatchAsync(batch, report, cancellationToken).ConfigureAwait(false);
        }
        return report;
    }

    public async Task<int> VerifyLinksAsync()
    {
        int missing = 0;
        foreach (var entry in _db.StreamLinkedButMissingOnDisk())
        {
            await _db.UpdateLink(entry.YoutubeId, null, null, LinkStatus.Missing);
            Log.Warning("File missing for entry {YoutubeId}: {Path}", entry.YoutubeId, entry.LinkedFilePath);
            missing++;
        }
        return missing;
    }

    private async Task ProcessBatchAsync(
        List<FileRecord> batch,
        LinkReport report,
        CancellationToken cancellationToken)
    {
        foreach (var file in batch)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (file.YoutubeId != null)
            {
                var entry = _db.FindByYoutubeId(file.YoutubeId);
                if (entry != null)
                {
                    if (entry.Status == LinkStatus.Linked && entry.LinkedFilePath == file.FilePath)
                    {
                        report.AlreadyLinked++;
                        continue;
                    }

                    await _db.UpdateLink(file.YoutubeId, file.FilePath, file.SizeBytes, LinkStatus.Linked);
                    Log.Information("Linked {YoutubeId} -> {FilePath}", file.YoutubeId, file.FilePath);
                    report.NewlyLinked++;
                }
                else
                {
                    Log.Debug("File {FilePath} has YouTube ID {id} but no DB entry", file.FilePath, file.YoutubeId);
                    report.UntrackedFiles++;
                }
            }
            else
            {
                Log.Debug("No YouTube ID found in file name: {FileName}", file.FileName);
                report.UnmatchedFiles++;
            }
        }
    }
}

public sealed class LinkReport
{
    public int NewlyLinked { get; set; }
    public int AlreadyLinked { get; set; }
    public int UnmatchedFiles { get; set; }
    public int UntrackedFiles { get; set; }

    public override string ToString() =>
        $"NewlyLinked={NewlyLinked}, AlreadyLinked={AlreadyLinked}, " +
        $"Unmatched={UnmatchedFiles}, Untracked={UntrackedFiles}";
}