using System.Text.RegularExpressions;
using Serilog;
using VideoArchiveManager.Interfaces;
using VideoArchiveManager.Configuration;
using VideoArchiveManager.Models;

namespace VideoArchiveManager.Services;

public sealed class ArchiveScanner
{
    // Matches YouTube IDs in the format [VIDEO_ID] at the end of the filename, optionally followed by an extension.
    private static readonly Regex YoutubeIdPattern =
        new(@"\[(?<id>[A-Za-z0-9_-]{11})\](?:\.[^.]+)?$", RegexOptions.Compiled);

    private readonly AppSettings _settings;
    private readonly IFileSystem _fileSystem;

    public ArchiveScanner(AppSettings settings, IFileSystem fileSystem)
    {
        _settings = settings;
        _fileSystem = fileSystem;
    }

    // Scans the archive directory and yields FileRecord objects for each video file found.
    public IEnumerable<FileRecord> Scan(CancellationToken cancellationToken = default)
    {
        if (!Directory.Exists(_settings.ArchiveRootPath))
        {
            Log.Warning("Archive root does not exist: {Path}", _settings.ArchiveRootPath);
            yield break;
        }

        var enumerationOptions = new EnumerationOptions
        {
            IgnoreInaccessible = true, // Skip files/directories we can't access
            RecurseSubdirectories = true, // Search all subdirectories
            AttributesToSkip = FileAttributes.System, // Skip system files
            BufferSize = 65536, // 64 KB
        };

        IEnumerable<string> files;
        try 
        {
            files = _fileSystem.EnumerateFiles(_settings.ArchiveRootPath);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to enumerate archive root: {Path}", _settings.ArchiveRootPath);
            yield break;
        }

        // Process each file found in the archive
        foreach (var filePath in files)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var ext = Path.GetExtension(filePath);
            if (!_settings.VideoExtensions.Contains(ext))
            {
                continue;
            }

            // Attempt to create a FileRecord for the video file, including extracting YouTube ID if present.
            FileRecord record;
            try
            {
                var fileSize = _fileSystem.GetFileSize(filePath);
                record = new FileRecord
                {
                    FilePath = filePath,
                    FileName = Path.GetFileName(filePath),
                    Extension = ext.ToLowerInvariant(),
                    SizeBytes = fileSize,
                    LastWriteTimeUtc = DateTime.UtcNow,
                    CreationTimeUtc = DateTime.UtcNow,
                    YoutubeId = ExtractYoutubeId(Path.GetFileName(filePath))
                };
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "Could not read file info for: {FilePath}", filePath);
                continue;
            }

            yield return record;
        }
    }

    // Extracts a YouTube video ID from the filename if it matches the expected pattern.
    public static string? ExtractYoutubeId(string fileName)
    {
        var match = YoutubeIdPattern.Match(fileName);
        return match.Success ? match.Groups["id"].Value : null;
    }
}