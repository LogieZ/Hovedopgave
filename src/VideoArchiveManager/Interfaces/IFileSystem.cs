using System.Collections.Generic;

namespace VideoArchiveManager.Interfaces;

public interface IFileSystem
{
    IEnumerable<string> EnumerateFiles(string path);
    bool FileExists(string path);
    long GetFileSize(string path);
}