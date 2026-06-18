using System.Diagnostics;
using System.Globalization;
using System.Text;
using VideoArchiveManager.Interfaces;

namespace VideoArchiveManager.Services;

/// <summary>
/// Abstract base class for platform-specific file system implementations.
/// Provides common file operations that work across Windows and Linux platforms.
/// Specific platform behavior (if any) can be overridden in derived classes.
/// </summary>
public abstract class FileSystemBase : IFileSystem
{
    public virtual IEnumerable<string> EnumerateFiles(string path)
    {
        return Directory.EnumerateFiles(path, "*.*", SearchOption.AllDirectories);
    }

    public virtual bool FileExists(string path) => File.Exists(path);

    public virtual long GetFileSize(string path) => new FileInfo(path).Length;

    public virtual Task<long> GetDurationSecondsAsync(string path) => ReadDurationSecondsAsync(path);

    /// <summary>
    /// Reads the duration of a video file in seconds using ffprobe.
    /// This implementation is platform-agnostic and used by both Windows and Linux implementations.
    /// </summary>
    protected static async Task<long> ReadDurationSecondsAsync(string path)
    {
        try
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = "ffprobe",
                Arguments = $"-v error -show_entries format=duration -of default=noprint_wrappers=1:nokey=1 \"{path}\"",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8
            };

            using var process = Process.Start(startInfo);
            if (process == null)
            {
                return 0;
            }

            Task<string> outputTask = process.StandardOutput.ReadToEndAsync();
            await process.WaitForExitAsync();
            var output = (await outputTask).Trim();

            if (process.ExitCode != 0 || !double.TryParse(output, NumberStyles.Float, CultureInfo.InvariantCulture, out var durationSeconds))
            {
                return 0;
            }

            return (long)Math.Round(durationSeconds, MidpointRounding.AwayFromZero);
        }
        catch
        {
            return 0;
        }
    }
}
