using Reqnroll;
using FluentAssertions;
using NSubstitute;
using VideoArchiveManager.Interfaces;
using VideoArchiveManager.Models;
using VideoArchiveManager.Configuration;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace VideoArchiveManager.Tests.StepDefinitions;

[Binding]
public class SyncMissingFilesSteps
{
    private readonly IDatabaseService _mockDb = Substitute.For<IDatabaseService>();
    private readonly IFileSystem _mockFileSystem = Substitute.For<IFileSystem>();
    private readonly AppSettings _settings;
    private VideoEntry? _fakeDbEntry;
    private string _fakeFilePath = @"F:\DKCTV Filer\Manglende_Video.mp4";
    private readonly List<VideoEntry> _mixedEntries = new();

    public SyncMissingFilesSteps()
    {
        _settings = new AppSettings 
        { 
            ArchiveRootPath = @"F:\DKCTV Filer",
            BatchSize = 10,
            VideoExtensions = new HashSet<string> { ".mp4"}
        };
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

    [Given(@"the file exists on disk")]
    public void GivenTheFileExistsOnDisk()
    {
        _mockFileSystem.FileExists(_fakeFilePath).Returns(true);
    }

    [Given(@"mixed linked records exist in the database")]
    public void GivenMixedLinkedRecordsExistInTheDatabase()
    {
        _mixedEntries.Clear();

        var missingPath = @"F:\DKCTV Filer\Manglende_Video_1.mp4";
        var existingPath = @"F:\DKCTV Filer\Eksisterende_Video_1.mp4";

        _mixedEntries.Add(new VideoEntry
        {
            YoutubeId = "missing001",
            Title = "Manglende video",
            Status = LinkStatus.Linked,
            LinkedFilePath = missingPath
        });

        _mixedEntries.Add(new VideoEntry
        {
            YoutubeId = "existing001",
            Title = "Eksisterende video",
            Status = LinkStatus.Linked,
            LinkedFilePath = existingPath
        });

        _mockDb.StreamLinkedButMissingOnDisk().Returns(_mixedEntries);
    }

    [Given(@"one linked file is missing on disk and one exists")]
    public void GivenOneLinkedFileIsMissingOnDiskAndOneExists()
    {
        _mockFileSystem.FileExists(@"F:\DKCTV Filer\Manglende_Video_1.mp4").Returns(false);
        _mockFileSystem.FileExists(@"F:\DKCTV Filer\Eksisterende_Video_1.mp4").Returns(true);
    }

    [When(@"the synchronization process starts")]
    public async Task WhenTheSynchronizationProcessStarts()
    {
        // Simulate the missing-file verification phase from Program.cs.
        var missingVideos = _mockDb.StreamLinkedButMissingOnDisk();
        foreach (var video in missingVideos)
        {
            if (string.IsNullOrWhiteSpace(video.LinkedFilePath))
            {
                continue;
            }

            if (!_mockFileSystem.FileExists(video.LinkedFilePath))
            {
                video.MarkAsMissing();
                await _mockDb.UpdateLink(video.YoutubeId, null, null, LinkStatus.Missing);
            }
        }
    }

    [Then(@"the database entry should be marked as ""Missing""")]
    public async Task ThenTheDatabaseEntryShouldBeMarkedAsMissing()
    {
        await _mockDb.Received(1).UpdateLink(_fakeDbEntry!.YoutubeId, null, null, LinkStatus.Missing);

        _fakeDbEntry.Status.Should().Be(LinkStatus.Missing);
        _fakeDbEntry.LinkedFilePath.Should().BeNull();
    }

    [Then(@"no database entry should be marked as ""Missing""")]
    public async Task ThenNoDatabaseEntryShouldBeMarkedAsMissing()
    {
        await _mockDb.DidNotReceive().UpdateLink(
            Arg.Any<string>(),
            Arg.Any<string?>(),
            Arg.Any<long?>(),
            LinkStatus.Missing);
    }

    [Then(@"exactly one database entry should be marked as ""Missing""")]
    public async Task ThenExactlyOneDatabaseEntryShouldBeMarkedAsMissing()
    {
        await _mockDb.Received(1).UpdateLink(
            Arg.Any<string>(),
            Arg.Any<string?>(),
            Arg.Any<long?>(),
            LinkStatus.Missing);
    }
}