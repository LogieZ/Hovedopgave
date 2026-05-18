using FluentAssertions;
using NSubstitute;
using VideoArchiveManager.Configuration;
using VideoArchiveManager.Interfaces;
using VideoArchiveManager.Services;

namespace VideoArchiveManager.Tests;

public sealed class ArchiveScannerTests : IDisposable
{
    private readonly string _tempArchiveRoot;
    private readonly IFileSystem _fileSystem;
    private readonly AppSettings _settings;

    public ArchiveScannerTests()
    {
        _tempArchiveRoot = Path.Combine(Path.GetTempPath(), $"video-archive-tests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempArchiveRoot);

        _fileSystem = Substitute.For<IFileSystem>();
        _settings = new AppSettings
        {
            ArchiveRootPath = _tempArchiveRoot,
            VideoExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { ".mp4" }
        };
    }

    [Fact]
    public void Scan_WhenArchiveIsEmpty_ShouldReturnNoRecords()
    {
        _fileSystem.EnumerateFiles(_tempArchiveRoot).Returns(Array.Empty<string>());

        var scanner = new ArchiveScanner(_settings, _fileSystem);
        var result = scanner.Scan().ToList();

        result.Should().BeEmpty();
    }

    [Fact]
    public void Scan_WhenFileHasNoExtension_ShouldIgnoreFile()
    {
        var noExtensionFile = Path.Combine(_tempArchiveRoot, "video_without_extension");
        _fileSystem.EnumerateFiles(_tempArchiveRoot).Returns(new[] { noExtensionFile });

        var scanner = new ArchiveScanner(_settings, _fileSystem);
        var result = scanner.Scan().ToList();

        result.Should().BeEmpty();
        _fileSystem.DidNotReceive().GetFileSize(noExtensionFile);
    }

    [Fact]
    public void Scan_WhenFileIsNotVideoExtension_ShouldIgnoreFile()
    {
        var textFile = Path.Combine(_tempArchiveRoot, "notes.txt");
        _fileSystem.EnumerateFiles(_tempArchiveRoot).Returns(new[] { textFile });

        var scanner = new ArchiveScanner(_settings, _fileSystem);
        var result = scanner.Scan().ToList();

        result.Should().BeEmpty();
        _fileSystem.DidNotReceive().GetFileSize(textFile);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempArchiveRoot))
        {
            Directory.Delete(_tempArchiveRoot, recursive: true);
        }
    }
}
