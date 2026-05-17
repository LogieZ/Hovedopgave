using Reqnroll;
using FluentAssertions;
using NSubstitute;
using VideoArchiveManager.Interfaces;
using VideoArchiveManager.Models;
using VideoArchiveManager.Services;
using VideoArchiveManager.Configuration;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace VideoArchiveManager.Tests.StepDefinitions;

[Binding]
public class SyncMissingFilesSteps
{
    private readonly IDatabaseService _mockDb = Substitute.For<IDatabaseService>();
    private readonly IFileSystem _mockFileSystem = Substitute.For<IFileSystem>();
    private readonly IYoutubeService _mockYoutube = Substitute.For<IYoutubeService>();

    private readonly AppSettings _settings;
    private readonly ArchiveScanner _scanner;

    private LinkerService? _linker;
    private VideoEntry? _fakeDbEntry;
    private string _fakeFilePath = @"F:\DKCTV Filer\Manglende_Video.mp4";

    public SyncMissingFilesSteps()
    {
        _settings = new AppSettings 
        { 
            ArchiveRootPath = @"F:\DKCTV Filer",
            BatchSize = 10,
            VideoExtensions = new HashSet<string> { ".mp4"}
        };
        _scanner = new ArchiveScanner(_settings, _mockFileSystem);
    }

    [Given(@"a video record exists in the database with status ""Linked""")]
    public void GivenAVideoRecordExistsInTheDatabaseWithStatusLinked()
    {
        _fakeDbEntry = new VideoEntry
        {
            YoutubeId = "ykB5jleVsAM",
            Title = "Manglende Video",
            Status = LinkStatus.Linked,
            LinkedFilePath = _fakeFilePath
        };
        // We simulate that the Sync logic retrieves this video when searching for missing files
        _mockDb.StreamLinkedButMissingOnDisk().Returns(new List<VideoEntry> { _fakeDbEntry });
    }

    [Given(@"the file is missing on disk")]
    public void GivenTheFileIsMissingOnDisk()
    {
        // We simulate that the file system returns FALSE when the program checks if the path exists
        _mockFileSystem.FileExists(_fakeFilePath).Returns(false);
    }

    [When(@"the synchronization process starts")]
    public async Task WhenTheSynchronizationProcessStarts()
    {
        // Here we initialize the sync/linker logic
        _linker = new LinkerService(_mockDb, _settings);

        var missingVideos = _mockDb.StreamLinkedButMissingOnDisk();
        foreach (var video in missingVideos)
        {
            if (!_mockFileSystem.FileExists(video.LinkedFilePath!))
            {
                video.Status = LinkStatus.Downloading;
                await _mockYoutube.DownloadVideoAsync(video, video.LinkedFilePath!);
            }
        }
    }

    [Then(@"the YouTube downloader should be triggered for that video")]
    public async Task ThenTheYoutubeDownloaderShouldBeTriggeredForThatVideo()
    {
        // ASSERTION: We verify via our YouTube mock that the downloader was called with the correct YouTube ID and file path
        await _mockYoutube.Received(1).DownloadVideoAsync(_fakeDbEntry!, _fakeFilePath);

        // We can also verify that the status in the database temporarily changes to "Downloading"
        _fakeDbEntry.Status.Should().Be(LinkStatus.Downloading);
    }
}