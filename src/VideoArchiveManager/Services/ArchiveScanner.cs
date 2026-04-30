using System.Text.RegularExpressions;
using Serilog;
using VideoArchiveManager.Models;
using VideoArchiveManager.Configuration;

namespace VideoArchiveManager.Services;

public sealed class ArchiveScanner
{
    private static readonly Regex YoutubeIdPattern =
        new(@"\[(?<id>[A-Za-z0-9_-]{11})\](?:\.[^.]+)?$", RegexOptions.Compiled);

    private readonly AppSettings _settings;

    public ArchiveScanner(AppSettings settings)
    {
        _settings = settings;
    }

    public IEnumerable<FileRecord> Scan(CancellationToken cancellationToken = default)
    {
        if (!Directory.Exists(_settings.ArchiveRootPath))
        {
            Log.Warning("Archive root does not exist: {Path}", _settings.ArchiveRootPath);
            yield break;
        }

        var enumerationOptions = new EnumerationOptions
        {
            IgnoreInaccessible = true,
            RecurseSubdirectories = true,
            AttributesToSkip = FileAttributes.System,
            BufferSize = 65536, // 64 KB
        };

        IEnumerable<string> files;
        try 
        {
            files = Directory.EnumerateFiles(
                _settings.ArchiveRootPath,
                "*",
                enumerationOptions);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to enumerate archive root: {Path}", _settings.ArchiveRootPath);
            yield break;
        }

        foreach (var filePath in files)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var ext = Path.GetExtension(filePath);
            if (!_settings.VideoExtensions.Contains(ext))
            {
                continue;
            }

            FileRecord record;
            try
            {
                var info = new FileInfo(filePath);
                record = new FileRecord
                {
                    FilePath = filePath,
                    FileName = info.Name,
                    Extension = ext.ToLowerInvariant(),
                    SizeBytes = info.Length,
                    LastWriteTimeUtc = info.LastWriteTimeUtc,
                    CreationTimeUtc = info.CreationTimeUtc,
                    YoutubeId = ExtractYoutubeId(info.Name)
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

    public static string? ExtractYoutubeId(string fileName)
    {
        var match = YoutubeIdPattern.Match(fileName);
        return match.Success ? match.Groups["id"].Value : null;
    }
}