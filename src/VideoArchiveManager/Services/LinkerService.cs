using Serilog;
using VideoArchiveManager.Models;
using VideoArchiveManager.Configuration;
using VideoArchiveManager.Interfaces;

namespace VideoArchiveManager.Services;

public sealed class LinkerService
{
    private readonly IDatabaseService _db;
    private readonly AppSettings _settings;
    private readonly IFileSystem _fileSystem;

    public LinkerService(IDatabaseService db, AppSettings settings, IFileSystem fileSystem)
    {
        _db = db;
        _settings = settings;
        _fileSystem = fileSystem;
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

        // After processing all files, we can also check for any untracked files in the database that might need attention.
        if (report.UnmatchedFileNames.Any())
        {
            await File.WriteAllLinesAsync("unmatched_files.txt", report.UnmatchedFileNames, cancellationToken);
            Log.Information("A list of {Count} unmatched files has been saved to 'unmatched_files.txt'", report.UnmatchedFileNames.Count);
        }

        return report;
    }

    private async Task ProcessBatchAsync(
        List<FileRecord> batch,
        LinkReport report,
        CancellationToken cancellationToken)
    {
        foreach (var file in batch)
        {
            cancellationToken.ThrowIfCancellationRequested();
            // First pass: cheap title-only matching (no ffprobe duration yet).
            var entry = _db.FindBestMatchByTitle(file.FileName, 0, file.FilePath);

            if (entry != null)
            {
                if (entry.Status == LinkStatus.Linked)
                {
                    report.AlreadyLinked++;
                    continue;
                }

                // Second pass: only now resolve duration and reconfirm match.
                long durationSeconds = file.Duration;
                if (durationSeconds <= 0)
                {
                    durationSeconds = await _fileSystem.GetDurationSecondsAsync(file.FilePath);
                }

                if (durationSeconds > 0)
                {
                    var durationVerifiedEntry = _db.FindBestMatchByTitle(file.FileName, durationSeconds, file.FilePath);
                    if (durationVerifiedEntry == null)
                    {
                        report.UnmatchedFileNames.Add(file.FileName);
                        continue;
                    }

                    entry = durationVerifiedEntry;
                }

                await _db.UpdateLink(entry.YoutubeId, file.FilePath, file.SizeBytes, LinkStatus.Linked);
                Log.Information("Match found: [DB: {DBTitle}] <-> [File: {FileName}]", entry.Title, file.FileName);
                report.NewlyLinked++;
            }
            else 
            {
                Log.Debug("Could not match file: {FileName}", file.FileName);
                // Add the filename to the list in the report for unmatched files, so we can review it later.
                report.UnmatchedFileNames.Add(file.FileName);
            }
        }
    }
}

public sealed class LinkReport
{
    public int NewlyLinked { get; set; }
    public int AlreadyLinked { get; set; }
    // We store the actual filenames in a list instead of just a count, so we can review them later and see if there are any patterns in the unmatched files (e.g., certain naming conventions that are not being parsed correctly).
    public List<string> UnmatchedFileNames { get; } = new(); 
    public int UntrackedFiles { get; set; }

    public override string ToString() =>
        $"NewlyLinked={NewlyLinked}, AlreadyLinked={AlreadyLinked}, " +
        $"Unmatched={UnmatchedFileNames.Count}, Untracked={UntrackedFiles}";
}