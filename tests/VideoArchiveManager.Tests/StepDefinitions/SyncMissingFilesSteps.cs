using Reqnroll;
using FluentAssertions;
using NSubstitute;
using VideoArchiveManager.Interfaces;
using VideoArchiveManager.Models;
using VideoArchiveManager.Services;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace VideoArchiveManager.Tests.StepDefinitions;

[Binding]
public class SyncMissingFilesSteps
{
    private readonly IDatabaseService _mockDb = Substitute.For<IDatabaseService>();
    private readonly IFileSystem _mockFileSystem = Substitute.For<IFileSystem>();
    private readonly IYoutubeService _mockYoutube = Substitute.For<IYoutubeService>();

    private readonly MissingFileSynchronizationService _syncService;

    private VideoEntry? _fakeDbEntry;
    private readonly string _fakeFilePath = @"F:\DKCTV Filer\Manglende_Video.mp4";
    private List<VideoEntry> _multipleEntries = new();

    public SyncMissingFilesSteps()
    {
        _syncService = new MissingFileSynchronizationService(_mockDb, _mockFileSystem, _mockYoutube);
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
        _mockYoutube.DownloadVideoAsync(Arg.Any<VideoEntry>(), Arg.Any<string>()).Returns(true);
    }

    [When(@"the synchronization process starts")]
    public async Task WhenTheSynchronizationProcessStarts()
    {
        await _syncService.SyncMissingLinkedFilesAsync();
    }

    [Then(@"the YouTube downloader should be triggered for that video")]
    public async Task ThenTheYoutubeDownloaderShouldBeTriggeredForThatVideo()
    {
        _fakeDbEntry.Should().NotBeNull();
        var expectedDestinationFolder = Path.GetDirectoryName(_fakeFilePath)!;
        await _mockYoutube.Received(1).DownloadVideoAsync(_fakeDbEntry!, expectedDestinationFolder);

        _fakeDbEntry.Status.Should().Be(LinkStatus.Downloading);
        _mockDb.Received().UpdateVideoEntry(_fakeDbEntry!);
    }

    [Given(@"the YouTube downloader fails")]
    public void GivenTheYoutubeDownloaderFails()
    {
        _mockYoutube.DownloadVideoAsync(Arg.Any<VideoEntry>(), Arg.Any<string>()).Returns(false);
    }

    [Then(@"the video status should be set to ""DownloadFailed""")]
    public void ThenTheVideoStatusShouldBeSetToDownloadFailed()
    {
        _fakeDbEntry.Should().NotBeNull();
        _fakeDbEntry.Status.Should().Be(LinkStatus.DownloadFailed);
    }

    [Then(@"the database should be updated with the new status")]
    public void ThenTheDatabaseShouldBeUpdatedWithTheNewStatus()
    {
        _mockDb.Received().UpdateVideoEntry(_fakeDbEntry!);
    }

    [Given(@"multiple video records exist with status ""Linked"" and missing files")]
    public void GivenMultipleVideoRecordsExistWithStatusLinkedAndMissingFiles()
    {
        _multipleEntries = new()
        {
            new VideoEntry
            {
                YoutubeId = "video1",
                Title = "Video 1",
                Status = LinkStatus.Linked,
                LinkedFilePath = @"F:\DKCTV Filer\Video1.mp4"
            },
            new VideoEntry
            {
                YoutubeId = "video2",
                Title = "Video 2",
                Status = LinkStatus.Linked,
                LinkedFilePath = @"F:\DKCTV Filer\Video2.mp4"
            },
            new VideoEntry
            {
                YoutubeId = "video3",
                Title = "Video 3",
                Status = LinkStatus.Linked,
                LinkedFilePath = @"F:\DKCTV Filer\Video3.mp4"
            }
        };

        _mockDb.StreamLinkedButMissingOnDisk().Returns(_multipleEntries);
        _mockFileSystem.FileExists(Arg.Any<string>()).Returns(false);
        _mockYoutube.DownloadVideoAsync(Arg.Any<VideoEntry>(), Arg.Any<string>()).Returns(true);
    }

    [Then(@"all missing files should trigger a download attempt")]
    public async Task ThenAllMissingFilesShouldTriggerADownloadAttempt()
    {
        _multipleEntries.Should().HaveCount(3);
        
        foreach (var entry in _multipleEntries)
        {
            var destinationFolder = Path.GetDirectoryName(entry.LinkedFilePath!)!;
            await _mockYoutube.Received(1).DownloadVideoAsync(entry, destinationFolder);
        }
    }

    [Given(@"a video record exists with a null LinkedFilePath")]
    public void GivenAVideoRecordExistsWithNullLinkedFilePath()
    {
        _fakeDbEntry = new VideoEntry
        {
            YoutubeId = "nullPathVideo",
            Title = "Video with Null Path",
            Status = LinkStatus.Linked,
            LinkedFilePath = null
        };

        _mockDb.StreamLinkedButMissingOnDisk().Returns(new List<VideoEntry> { _fakeDbEntry });
    }

    [Then(@"the video should be skipped gracefully")]
    public async Task ThenTheVideoShouldBeSkippedGracefully()
    {
        await _mockYoutube.DidNotReceive().DownloadVideoAsync(Arg.Any<VideoEntry>(), Arg.Any<string>());
    }

    [Given(@"the file exists on disk")]
    public void GivenTheFileExistsOnDisk()
    {
        _mockFileSystem.FileExists(_fakeFilePath).Returns(true);
        _fakeDbEntry = new VideoEntry
        {
            YoutubeId = "existingFile",
            Title = "Existing Video",
            Status = LinkStatus.Linked,
            LinkedFilePath = _fakeFilePath
        };
        _mockDb.StreamLinkedButMissingOnDisk().Returns(new List<VideoEntry> { _fakeDbEntry });
    }

    [Then(@"no download should be triggered for that video")]
    public async Task ThenNoDownloadShouldBeTriggeredForThatVideo()
    {
        await _mockYoutube.DidNotReceive().DownloadVideoAsync(Arg.Any<VideoEntry>(), Arg.Any<string>());
    }
}