using System.Diagnostics;
using System.Text.Json;
using System.Collections.Generic;
using Serilog;
using VideoArchiveManager.Interfaces;
using VideoArchiveManager.Configuration;
using VideoArchiveManager.Models;

namespace VideoArchiveManager.Services;

public sealed class YoutubeService : IYoutubeService
{
    private readonly AppSettings _settings;
    private readonly IDatabaseService _db;
    private Dictionary<string, DateTime?> _latestChannelDateIndex = new(StringComparer.OrdinalIgnoreCase);

    public YoutubeService(AppSettings settings, IDatabaseService db)
    {
        _settings = settings;
        _db = db;
    }

    public async Task ImportChannelVideosAsync(string channelUrl)
    {
        // Use flat playlist mode for speed on large channels. This avoids resolving full metadata
        // per video up front, which can make startup appear stuck for a long time.
        var startInfo = new ProcessStartInfo
        {
            FileName = _settings.YtDlpPath,
            Arguments = $"--flat-playlist --dump-json --ignore-errors --no-warnings --cookies cookies.txt {channelUrl}",
            RedirectStandardOutput = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = System.Text.Encoding.UTF8
        };

        using var process = Process.Start(startInfo) ?? throw new Exception("Kunne ikke starte yt-dlp");
        using var reader = process.StandardOutput;
        int importedCount = 0;
        var dateIndex = new Dictionary<string, DateTime?>(StringComparer.OrdinalIgnoreCase);

        while (!reader.EndOfStream)
        {
            var line = await reader.ReadLineAsync();
            if (string.IsNullOrWhiteSpace(line)) continue;

            // yt-dlp may print warnings/info lines; only JSON objects are video metadata.
            if (!line.TrimStart().StartsWith("{"))
            {
                continue;
            }

            try 
            {
                using var doc = JsonDocument.Parse(line);
                var root = doc.RootElement;

                string title = root.TryGetProperty("title", out var t) ? t.GetString() ?? "No title" : "No title";
                string id = root.TryGetProperty("id", out var i) ? i.GetString() ?? "" : "";
                
                long duration = 0;
                if (root.TryGetProperty("duration", out var durProp) && durProp.ValueKind == JsonValueKind.Number)
                {
                    duration = (long)durProp.GetDouble();
                }

                DateTime? uploadedDate = ParseUploadedDateFromJson(root);

                if (string.IsNullOrEmpty(id)) continue;

                dateIndex[id] = uploadedDate;

                var entry = new VideoEntry
                {
                    YoutubeId = id,
                    Title = title,
                    DurationSeconds = duration,
                    UploadedDate = uploadedDate,
                    Status = LinkStatus.Unlinked
                };

                _db.AddVideoEntry(entry);
                importedCount++;

                if (importedCount % 250 == 0)
                {
                    Log.Information("Metadata import progress: {Count} entries processed...", importedCount);
                }
            }
            catch (Exception ex)
            {
                // Keep import resilient: skip bad rows without surfacing as CLI errors.
                Log.Debug("Skipping video row during import due to DB/parse issue: {Message}", ex.Message);
            }
        }

        await process.WaitForExitAsync();
        _latestChannelDateIndex = dateIndex;
        Log.Information("Metadata import complete: processed {Count} entries.", importedCount);
    }

    public async Task<int> EnrichMissingMetadataAsync(int limit, CancellationToken cancellationToken = default)
    {
        if (limit <= 0)
        {
            return 0;
        }

        var targets = await _db.GetEntriesMissingUploadedDateAsync(limit);
        if (targets.Count == 0)
        {
            return 0;
        }

        var channelDates = _latestChannelDateIndex;
        if (channelDates.Count == 0)
        {
            Log.Information("Metadata enrichment skipped: no channel date index available from current import run.");
            return 0;
        }

        int indexWithDate = channelDates.Values.Count(d => d != null);
        Log.Information(
            "Metadata enrichment source coverage: {WithDate}/{Total} entries in channel index have a usable date.",
            indexWithDate,
            channelDates.Count);

        int updated = 0;
        int processed = 0;
        int noMetadata = 0;
        var unresolved = new List<VideoEntry>();
        var fallbackReasonCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        foreach (var entry in targets)
        {
            cancellationToken.ThrowIfCancellationRequested();
            processed++;

            try
            {
                if (!channelDates.TryGetValue(entry.YoutubeId, out var parsedDate) || parsedDate == null)
                {
                    noMetadata++;
                    unresolved.Add(entry);
                    continue;
                }

                bool changed = false;

                if (entry.UploadedDate == null)
                {
                    entry.UploadedDate = parsedDate;
                    changed = true;
                }

                if (changed)
                {
                    await _db.UpdateVideoEntryAsync(entry);
                    updated++;
                }
                else
                {
                    noMetadata++;
                    unresolved.Add(entry);
                }

                if (processed % 25 == 0)
                {
                    Log.Information("Metadata enrichment progress: processed {Processed}/{Total}, updated {Updated}", processed, targets.Count, updated);
                }
            }
            catch (Exception ex)
            {
                Log.Warning("Metadata enrichment failed for {YoutubeId}: {Message}", entry.YoutubeId, ex.Message);
                unresolved.Add(entry);
            }
        }

        // Fallback phase: try direct lookups for unresolved entries.
        // If the channel index has no usable dates, increase fallback budget for this run.
        int fallbackBudget = indexWithDate == 0 ? Math.Min(limit, 50) : 10;
        int fallbackLimit = Math.Min(fallbackBudget, unresolved.Count);
        int fallbackUpdated = 0;

        // Shuffle unresolved entries so repeated runs do not always retry the same subset first.
        var fallbackCandidates = unresolved
            .OrderBy(_ => Random.Shared.Next())
            .Take(fallbackLimit)
            .ToList();

        for (int i = 0; i < fallbackCandidates.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var entry = fallbackCandidates[i];
            var fallbackResult = await TryFetchUploadDateForVideoAsync(entry.YoutubeId, cancellationToken);
            if (!fallbackReasonCounts.TryAdd(fallbackResult.Reason, 1))
            {
                fallbackReasonCounts[fallbackResult.Reason]++;
            }

            if (fallbackResult.Date == null && !string.IsNullOrWhiteSpace(fallbackResult.DiagnosticMessage))
            {
                Log.Debug("Upload date fetch failed for {YoutubeId}: {Reason} - yt-dlp stderr: {Details}", entry.YoutubeId, fallbackResult.Reason, fallbackResult.DiagnosticMessage);
            }

            var parsedDate = fallbackResult.Date;
            if (parsedDate == null)
            {
                // Even failed requests count toward rate limit. Add delay before next attempt.
                if (i < fallbackCandidates.Count - 1)
                {
                    await Task.Delay(7000, cancellationToken);
                }
                continue;
            }

            entry.UploadedDate = parsedDate;
            await _db.UpdateVideoEntryAsync(entry);
            fallbackUpdated++;

            // YouTube rate limit applies to all requests (success or fail). Add delay between attempts.
            // YouTube docs recommend 5-10 seconds; using 7 seconds as middle ground.
            if (i < fallbackCandidates.Count - 1)
            {
                await Task.Delay(7000, cancellationToken);
            }
        }

        if (fallbackLimit > 0)
        {
            updated += fallbackUpdated;
            noMetadata -= fallbackUpdated;
            if (noMetadata < 0) noMetadata = 0;
        }

        Log.Information(
            "Metadata enrichment summary: processed {Processed}, updated {Updated}, no new date/info {NoMetadata}, fallback checked {FallbackChecked}, fallback updated {FallbackUpdated}",
            processed,
            updated,
            noMetadata,
            fallbackLimit,
            fallbackUpdated);

        if (fallbackReasonCounts.Count > 0)
        {
            var reasonText = string.Join(", ", fallbackReasonCounts.OrderByDescending(kv => kv.Value).Select(kv => $"{kv.Key}={kv.Value}"));
            Log.Information("Metadata enrichment fallback reasons: {Reasons}", reasonText);
        }

        return updated;
    }

    private async Task<(DateTime? Date, string Reason, string DiagnosticMessage)> TryFetchUploadDateForVideoAsync(string youtubeId, CancellationToken cancellationToken)
    {
        try
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = _settings.YtDlpPath,
                Arguments = $"--dump-single-json --sleep-interval 5 --ignore-errors --no-warnings --cookies cookies.txt --no-playlist \"https://www.youtube.com/watch?v={youtubeId}\"",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                StandardOutputEncoding = System.Text.Encoding.UTF8
            };

            using var process = Process.Start(startInfo);
            if (process == null)
            {
                return (null, "process_start_failed", "");
            }

            string output = await process.StandardOutput.ReadToEndAsync();
            string errorOutput = await process.StandardError.ReadToEndAsync() ?? string.Empty;

            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(TimeSpan.FromSeconds(20));
            await process.WaitForExitAsync(timeoutCts.Token);

            if (process.ExitCode != 0 || string.IsNullOrWhiteSpace(output))
            {
                if (process.ExitCode != 0)
                {
                    var lowerError = errorOutput.ToLowerInvariant();
                    if (lowerError.Contains("private") || lowerError.Contains("login"))
                    {
                        return (null, "private_or_auth", errorOutput);
                    }

                    if (lowerError.Contains("unavailable") || lowerError.Contains("not available") || lowerError.Contains("deleted"))
                    {
                        return (null, "unavailable_or_deleted", errorOutput);
                    }

                    return (null, "yt_dlp_nonzero_exit", errorOutput);
                }

                return (null, "empty_output", "");
            }

            using var doc = JsonDocument.Parse(output);
            var date = ParseUploadedDateFromJson(doc.RootElement);
            return date == null ? (null, "no_date_fields", "") : (date, "success", "");
        }
        catch (OperationCanceledException)
        {
            return (null, "timeout_or_cancelled", "");
        }
        catch (JsonException)
        {
            return (null, "invalid_json", "");
        }
        catch
        {
            return (null, "exception", "");
        }
    }

    private static DateTime? ParseUploadedDateFromJson(JsonElement root)
    {
        if (root.TryGetProperty("upload_date", out var uploadDateProp) && uploadDateProp.ValueKind == JsonValueKind.String)
        {
            var parsed = ParseUploadDate(uploadDateProp.GetString());
            if (parsed != null)
            {
                return parsed;
            }
        }

        if (root.TryGetProperty("timestamp", out var timestampProp) && timestampProp.ValueKind == JsonValueKind.Number)
        {
            if (timestampProp.TryGetInt64(out long ts) && ts > 0)
            {
                return DateTimeOffset.FromUnixTimeSeconds(ts).UtcDateTime;
            }
        }

        if (root.TryGetProperty("release_timestamp", out var releaseTimestampProp) && releaseTimestampProp.ValueKind == JsonValueKind.Number)
        {
            if (releaseTimestampProp.TryGetInt64(out long rts) && rts > 0)
            {
                return DateTimeOffset.FromUnixTimeSeconds(rts).UtcDateTime;
            }
        }

        return null;
    }

    private static DateTime? ParseUploadDate(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw) || raw.Length != 8)
        {
            return null;
        }

        if (!DateTime.TryParseExact(raw, "yyyyMMdd", null, System.Globalization.DateTimeStyles.None, out var parsedDate))
        {
            return null;
        }

        return DateTime.SpecifyKind(parsedDate, DateTimeKind.Utc);
    }

    public async Task<bool> DownloadVideoAsync(VideoEntry entry, string destinationFolder, CancellationToken cancellationToken = default)
    {
        try {
            Log.Information("Starting download of: {Title} ({YoutubeId})", entry.Title, entry.YoutubeId);

            // Update status in DB to indicate download is in progress
            entry.MarkAsDownloading();
            await _db.UpdateVideoEntryAsync(entry);

            var startInfo = new ProcessStartInfo
            {
                FileName = _settings.YtDlpPath,
                // -f bestvideo+bestaudio/best: Downloads highest quality video and audio and merges to MP4
                // --merge-output-format mp4: Ensures output is in MP4 format
                // --restrict-filenames: Removes special characters from filename
                // -o: Specifies destination folder and filename format
                Arguments = $"--cookies cookies.txt -f \"bestvideo+bestaudio/best\" --merge-output-format mp4 " +
                            $"--restrict-filenames " +
                            $"--print after_move:filepath " +
                            $"-o \"{destinationFolder}/%(title)s.%(ext)s\" " +
                            $"--no-playlist \"https://www.youtube.com/watch?v={entry.YoutubeId}\"",
                RedirectStandardOutput = true,
                RedirectStandardError = false,
                UseShellExecute = false,
                CreateNoWindow = false
            };

            using var process = Process.Start(startInfo) ?? throw new Exception("Could not start yt-dlp");

            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(TimeSpan.FromMinutes(30)); // Set a timeout for the download to prevent it from hanging indefinitely

            Task<string> outputTask = process.StandardOutput.ReadToEndAsync();

            try
            {
                await process.WaitForExitAsync(timeoutCts.Token);
            }
            catch (OperationCanceledException)
            {
                process.Kill(true);
                Log.Warning("Download timed out for: {Title} - process killed", entry.Title);
                return false;
            }

            if (process.ExitCode == 0)
            {
                Log.Information("Download completed for: {Title}", entry.Title);

                string ytdlpOutput = await outputTask;
                var downloadedFilePath = ytdlpOutput
                    .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                    .LastOrDefault(line => File.Exists(line));
                    
                if (!string.IsNullOrWhiteSpace(downloadedFilePath))
                {
                    long? fileSizeBytes = null;
                    try
                    {
                        fileSizeBytes = new FileInfo(downloadedFilePath).Length;
                    }
                    catch
                    {
                        // Best-effort metadata; linking should still succeed without size.
                    }

                    entry.MarkAsLinked(downloadedFilePath, fileSizeBytes);
                }
                else
                {
                    Log.Warning(
                        "Download succeeded for {YoutubeId}, but output file path could not be resolved. Keeping status Unlinked for relinking.",
                        entry.YoutubeId);
                    entry.ResetFromStuckDownload();
                }

                await _db.UpdateVideoEntryAsync(entry);
                return true;
            }
            else
            {
                Log.Error("Download failed for: {Title} - Exit code: {ExitCode}", entry.Title, process.ExitCode);
                entry.MarkAsDownloadFailed();
                await _db.UpdateVideoEntryAsync(entry);
                return false;
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Error during download of {Id}", entry.YoutubeId);
            entry.MarkAsDownloadFailed();
            await _db.UpdateVideoEntryAsync(entry);
            return false;
        }
    }
}