using System.Text.RegularExpressions;
using Serilog;
using VideoArchiveManager.Interfaces;
using VideoArchiveManager.Configuration;
using VideoArchiveManager.Models;

namespace VideoArchiveManager.Services;

public sealed class ArchiveScanner
{
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
                    // Defer ffprobe duration until a plausible DB candidate exists.
                    Duration = 0,
                    SizeBytes = fileSize,
                    YoutubeId = null // Set to null by default, since local files won't have this info.
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
}