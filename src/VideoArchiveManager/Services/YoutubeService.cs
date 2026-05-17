using System.Diagnostics;
using System.Text.Json;
using Serilog;
using VideoArchiveManager.Interfaces;
using VideoArchiveManager.Configuration;
using VideoArchiveManager.Models;

namespace VideoArchiveManager.Services;

public sealed class YoutubeService : IYoutubeService
{
    private readonly AppSettings _settings;
    private readonly IDatabaseService _db;

    public YoutubeService(AppSettings settings, IDatabaseService db)
    {
        _settings = settings;
        _db = db;
    }

    public async Task ImportChannelVideosAsync(string channelUrl)
    {
        // We use yt-dlp to fetch the list of videos from the channel. The --flat-playlist option gives us a simple JSON output with basic info for each video, and we use --print to include the upload date in a separate line for easier parsing.
        var startInfo = new ProcessStartInfo
        {
            FileName = "yt-dlp",
            Arguments = $"--flat-playlist --dump-json --cookies cookies.txt --print \"upload_date:%(upload_date)s\" {channelUrl}",
            RedirectStandardOutput = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = System.Text.Encoding.UTF8
        };

        using var process = Process.Start(startInfo) ?? throw new Exception("Kunne ikke starte yt-dlp");
        using var reader = process.StandardOutput;

        while (!reader.EndOfStream)
        {
            var line = await reader.ReadLineAsync();
            if (string.IsNullOrWhiteSpace(line)) continue;
            if (line.StartsWith("upload_date:"))
            {
                // We store the date temporarily until the next JSON line comes, which contains the rest of the video info. This way we can associate the upload date with the correct video entry.
                var tempDateStr = line.Replace("upload_date:", "").Trim();
                
                // Read the NEXT line (which is the JSON part) and parse it to extract the video information.
                var jsonLine = await reader.ReadLineAsync();
                if (string.IsNullOrWhiteSpace(jsonLine)) continue;

                try 
                {
                    using var doc = JsonDocument.Parse(jsonLine);
                    var root = doc.RootElement;

                    string title = root.TryGetProperty("title", out var t) ? t.GetString() ?? "No title" : "No title";
                    string id = root.TryGetProperty("id", out var i) ? i.GetString() ?? "" : "";
                    
                    long duration = 0;
                    if (root.TryGetProperty("duration", out var durProp) && durProp.ValueKind == JsonValueKind.Number)
                    {
                        duration = (long)durProp.GetDouble();
                    }

                    // Parse the date we just captured from the line above. yt-dlp gives us the date in YYYYMMDD format, so we need to convert it to a DateTime object.
                    DateTime? uploadedDate = null;
                    if (tempDateStr.Length == 8 && DateTime.TryParseExact(tempDateStr, "yyyyMMdd", null, System.Globalization.DateTimeStyles.None, out var parsedDate))
                    {
                        uploadedDate = DateTime.SpecifyKind(parsedDate, DateTimeKind.Utc);
                    }

                    if (string.IsNullOrEmpty(id)) continue;

                    var entry = new VideoEntry
                    {
                        YoutubeId = id,
                        Title = title,
                        DurationSeconds = duration,
                        UploadedDate = uploadedDate,
                        Status = LinkStatus.Unlinked
                    };

                    _db.AddVideoEntry(entry);
                }
                catch (Exception ex)
                {
                    Log.Error("Error parsing video info: {Message}", ex.Message);
                }
            }
        }
    }

    public async Task<bool> DownloadVideoAsync(VideoEntry entry, string destinationFolder)
    {
        try {
            Log.Information("Starting download of: {Title} ({YoutubeId})", entry.Title, entry.YoutubeId);

            // Update status in DB to indicate download is in progress
            entry.Status = LinkStatus.Downloading;
            entry.UpdatedAt = DateTime.UtcNow;
            _db.UpdateVideoEntry(entry);

            var startInfo = new ProcessStartInfo
            {
                FileName = "yt-dlp",
                // -f bestvideo+bestaudio/best: Downloads highest quality video and audio and merges to MP4
                // --merge-output-format mp4: Ensures output is in MP4 format
                // --restrict-filenames: Removes special characters from filename
                // -o: Specifies destination folder and filename format
                Arguments = $"--cookies cookies.txt -f \"bestvideo+bestaudio/best\" --merge-output-format mp4 " +
                            $"--restrict-filenames " +
                            $"-o \"{destinationFolder}/%(title)s.%(ext)s\" " +
                            $"--no-playlist \"https://www.youtube.com/watch?v={entry.YoutubeId}\"",
                RedirectStandardOutput = false,
                RedirectStandardError = false,
                UseShellExecute = false,
                CreateNoWindow = false
            };

            using var process = Process.Start(startInfo) ?? throw new Exception("Could not start yt-dlp");

            await process.WaitForExitAsync();

            if (process.ExitCode == 0)
            {
                Log.Information("Download completed for: {Title}", entry.Title);
                entry.Status = LinkStatus.Linked;
                entry.UpdatedAt = DateTime.UtcNow;
                _db.UpdateVideoEntry(entry);
                return true;
            }
            else
            {
                Log.Error("Download failed for: {Title} - Exit code: {ExitCode}", entry.Title, process.ExitCode);
                entry.Status = LinkStatus.DownloadFailed;
                entry.UpdatedAt = DateTime.UtcNow;
                _db.UpdateVideoEntry(entry);
                return false;
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Error during download of {Id}", entry.YoutubeId);
            entry.Status = LinkStatus.DownloadFailed;
            entry.UpdatedAt = DateTime.UtcNow;
            _db.UpdateVideoEntry(entry);
            return false;
        }
    }
}