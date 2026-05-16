using VideoArchiveManager.Interfaces;

namespace VideoArchiveManager.Services;

public class LinuxFileSystem : IFileSystem
{
    public IEnumerable<string> EnumerateFiles(string path)
    {
        return Directory.EnumerateFiles(path, "*.*", SearchOption.AllDirectories);
    }

    public bool FileExists(string path) => File.Exists(path);

    public long GetFileSize(string path) => new FileInfo(path).Length;
}