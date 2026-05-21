using Xunit;
using FluentAssertions;
using NSubstitute;
using VideoArchiveManager.Interfaces;
using VideoArchiveManager.Services;
using VideoArchiveManager.Models;
using VideoArchiveManager.Configuration;
using System.Collections.Generic;

namespace VideoArchiveManager.Tests;

public class LinkerServiceTests
{
    private readonly IDatabaseService _mockDb;
    private readonly IFileSystem _mockFileSystem;
    private readonly AppSettings _settings;
    private readonly ArchiveScanner _scanner;
    private readonly LinkerService _linker;

    public LinkerServiceTests()
    {
        // Set up the mock database and file system
        _mockDb = Substitute.For<IDatabaseService>();
        _mockFileSystem = Substitute.For<IFileSystem>();

        _settings = new AppSettings 
        { 
            ArchiveRootPath = @"F:\DKCTV Filer",
            BatchSize = 10,
            VideoExtensions = new HashSet<string> { ".mp4"}
        };

        // Create the ArchiveScanner and LinkerService with the mocked dependencies
        _scanner = new ArchiveScanner(_settings, _mockFileSystem);
        _linker = new LinkerService(_mockDb, _settings, _mockFileSystem);
    }

    [Theory]
    // Scenarie 1: Perfekt match, standard filnavn
    [InlineData("Nyheder Fra Frederica mandag.mp4", "Nyheder fra Frederica mandag")]
    // Scenarie 2: Forskellig casing (Store/små bogstaver) og filendelse (.MP4)
    [InlineData("nyheder fra fredericia mandag.MP4", "Nyheder fra Frederica mandag")]
    // Scenarie 3: Ekstra mellemrum og underscores i filnavnet
    [InlineData("Nyheder_Fra_Frederica__mandag.mp4", "Nyheder fra Frederica mandag")]
    // Scenarie 4: Ekstra mærkelige mellemrum og rod i navnet
    [InlineData("Nyheder  Fra  Frederica   mandag.mp4", "Nyheder fra Frederica mandag")]

    public async Task LinkAsync_DDT_AcrossVariousFileScenarios_ShouldLinkSuccessfully(string realFileName, string dbTitle)
    {
        // Arrange
        var fakeFilePath = $@"F:\DKCTV Filer\{realFileName}";

        _settings.VideoExtensions = new HashSet<string> { ".mp4", ".MP4" };

        _mockFileSystem.EnumerateFiles(_settings.ArchiveRootPath)
            .Returns(new List<string> { fakeFilePath});
        _mockFileSystem.GetFileSize(fakeFilePath).Returns(1024L);

        var fakeDbEntry = new VideoEntry
        {
            YoutubeId = "abc123xyz45",
            Title = dbTitle,
            Status = LinkStatus.Unlinked
        };

        _mockDb.FindBestMatchByTitle(realFileName, Arg.Any<long>(), Arg.Any<string>()).Returns(fakeDbEntry);

        // Act
        var report = await _linker.LinkAsync(_scanner);

        // Assert
        report.NewlyLinked.Should().Be(1);
        report.UnmatchedFileNames.Should().BeEmpty();

        // Verify that the database was updated with the new link
        await _mockDb.Received(1).UpdateLink(
            "abc123xyz45", 
            fakeFilePath, 
            1024L, 
            LinkStatus.Linked);
    }

    [Fact]
    public async Task LinkAsync_WhenTitleDoesNotMatchAnything_ShouldAddToUnmatchedList()
    {
        // Arrange
        var fakeFilePath = @"F:\DKCTV Filer\Tilfældig Video.mp4";
        var fileNameOnly = "Tilfældig Video.mp4";

        _mockFileSystem.EnumerateFiles(_settings.ArchiveRootPath)
            .Returns(new List<string> { fakeFilePath});
        _mockFileSystem.GetFileSize(fakeFilePath).Returns(5000L);

        _mockDb.FindBestMatchByTitle(fileNameOnly, Arg.Any<long>(), Arg.Any<string>()).Returns((VideoEntry?)null);

        // Act
        var report = await _linker.LinkAsync(_scanner);

        // Assert
        report.NewlyLinked.Should().Be(0);
        report.UnmatchedFileNames.Should().Contain(fileNameOnly);

        // Verify that the database was not updated since there was no match
        await _mockDb.DidNotReceive().UpdateLink(
            Arg.Any<string>(), 
            Arg.Any<string>(), 
            Arg.Any<long>(), 
            Arg.Any<LinkStatus>());

        _mockFileSystem.DidNotReceive().GetDurationSeconds(Arg.Any<string>());
    }

    [Fact]
    public async Task LinkAsync_WhenFileIsCorruptedOrLocked_ShouldHandleExceptionGracefully()
    {
        // Arrange
        var fakeFilePath = @"F:\DKCTV Filer\Korrupt Video.mp4";

        _mockFileSystem.EnumerateFiles(_settings.ArchiveRootPath)
            .Returns(new List<string> { fakeFilePath});

        _mockFileSystem.GetFileSize(fakeFilePath)
            .Returns(_ => throw new IOException("The file is corrupted or locked by another process."));

        // Act
        var report = await _linker.LinkAsync(_scanner);

        // Assert
        report.NewlyLinked.Should().Be(0);

        await _mockDb.DidNotReceive().UpdateLink(
            Arg.Any<string>(), 
            Arg.Any<string>(), 
            Arg.Any<long>(), 
            Arg.Any<LinkStatus>());
    }

    [Fact]
    public async Task LinkAsync_WhenFileIsAlreadyLinked_ShouldSkipToOptimizePerformance()
    {
        // Arrange
        var fakeFilePath = @"F:\DKCTV Filer\Allerede Linked Video.mp4";
        var fileNameOnly = "Allerede Linked Video.mp4";

        _mockFileSystem.EnumerateFiles(_settings.ArchiveRootPath)
            .Returns(new List<string> { fakeFilePath});
        _mockFileSystem.GetFileSize(fakeFilePath).Returns(2048L);

        var alreadyLinkedEntry = new VideoEntry
        {
            YoutubeId = "def456uvw78",
            Title = "Allerede Linked Video",
            LinkedFilePath = fakeFilePath,
            Status = LinkStatus.Linked
        };
        _mockDb.FindBestMatchByTitle(fileNameOnly, Arg.Any<long>(), Arg.Any<string>()).Returns(alreadyLinkedEntry);

        // Act
        var report = await _linker.LinkAsync(_scanner);

        // Assert
        report.NewlyLinked.Should().Be(0);
        report.AlreadyLinked.Should().Be(1);

        await _mockDb.DidNotReceive().UpdateLink(
            Arg.Any<string>(), 
            Arg.Any<string>(), 
            Arg.Any<long>(), 
            Arg.Any<LinkStatus>());
    }

    [Fact]
    public async Task LinkAsync_WhenFileExtensionIsNotAllowed_ShouldIgnoreFile()
    {
        // Arrange
        var txtPath = @"F:\DKCTV Filer\NotAVideo.txt";
        _settings.VideoExtensions = new HashSet<string> { ".mp4" };

        _mockFileSystem.EnumerateFiles(_settings.ArchiveRootPath)
            .Returns(new List<string> { txtPath });

        // Act
        var report = await _linker.LinkAsync(_scanner);

        // Assert
        report.NewlyLinked.Should().Be(0);
        report.UnmatchedFileNames.Should().BeEmpty();

        _mockDb.DidNotReceive().FindByYoutubeId(Arg.Any<string>());
        _mockDb.DidNotReceive().FindBestMatchByTitle(Arg.Any<string>(), Arg.Any<long>(), Arg.Any<string>());
        await _mockDb.DidNotReceive().UpdateLink(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<long>(), Arg.Any<LinkStatus>());
    }

    [Fact]
    public async Task LinkAsync_WhenCancelled_ShouldThrowOperationCanceledException()
    {
        // Arrange
        var fakeFilePath = @"F:\DKCTV Filer\AnyVideo.mp4";
        _mockFileSystem.EnumerateFiles(_settings.ArchiveRootPath)
            .Returns(new List<string> { fakeFilePath });
        _mockFileSystem.GetFileSize(fakeFilePath).Returns(1000L);

        using var cts = new CancellationTokenSource();
        cts.Cancel();

        // Act
        Func<Task> act = async () => await _linker.LinkAsync(_scanner, cts.Token);

        // Assert
        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public async Task LinkAsync_WhenDatabaseUpdateFails_ShouldBubbleUpException()
    {
        // Arrange
        var fakeFilePath = @"F:\DKCTV Filer\Nyhedsudsendelse.mp4";
        _mockFileSystem.EnumerateFiles(_settings.ArchiveRootPath)
            .Returns(new List<string> { fakeFilePath });
        _mockFileSystem.GetFileSize(fakeFilePath).Returns(4096L);

        var match = new VideoEntry
        {
            YoutubeId = "abc123xyz45",
            Title = "Nyhedsudsendelse",
            Status = LinkStatus.Unlinked
        };

        _mockDb.FindBestMatchByTitle("Nyhedsudsendelse.mp4", Arg.Any<long>(), fakeFilePath).Returns(match);
        _mockDb.UpdateLink(match.YoutubeId, fakeFilePath, 4096L, LinkStatus.Linked)
            .Returns(_ => throw new InvalidOperationException("DB write failed"));

        // Act
        Func<Task> act = async () => await _linker.LinkAsync(_scanner);

        // Assert
        await act.Should().ThrowAsync<InvalidOperationException>();
    }
}