using FluentAssertions;
using NSubstitute;
using VideoArchiveManager.Configuration;
using VideoArchiveManager.Interfaces;
using VideoArchiveManager.Models;
using VideoArchiveManager.Services;

namespace VideoArchiveManager.Tests;

public class YoutubeServiceTests
{
    private readonly IDatabaseService _mockDb;
    private readonly YoutubeService _service;

    public YoutubeServiceTests()
    {
        _mockDb = Substitute.For<IDatabaseService>();
        var settings = new AppSettings
        {
            YtDlpPath = "yt-dlp"
        };

        _service = new YoutubeService(settings, _mockDb);
    }

    [Fact]
    public async Task EnrichMissingMetadataAsync_WhenLimitIsZero_ShouldReturnZeroAndDoNothing()
    {
        var result = await _service.EnrichMissingMetadataAsync(0);

        result.Should().Be(0);
        await _mockDb.DidNotReceive().GetEntriesMissingUploadedDateAsync(Arg.Any<int>());
        await _mockDb.DidNotReceive().UpdateVideoEntryAsync(Arg.Any<VideoEntry>());
    }

    [Fact]
    public async Task EnrichMissingMetadataAsync_WhenNoTargets_ShouldReturnZero()
    {
        _mockDb.GetEntriesMissingUploadedDateAsync(10).Returns(new List<VideoEntry>());

        var result = await _service.EnrichMissingMetadataAsync(10);

        result.Should().Be(0);
        await _mockDb.Received(1).GetEntriesMissingUploadedDateAsync(10);
        await _mockDb.DidNotReceive().UpdateVideoEntryAsync(Arg.Any<VideoEntry>());
    }

    [Fact]
    public async Task EnrichMissingMetadataAsync_WhenDateExistsInChannelIndex_ShouldUpdateEntry()
    {
        var entry = new VideoEntry
        {
            YoutubeId = "abc123",
            Title = "Test Video",
            UploadedDate = null
        };

        _mockDb.GetEntriesMissingUploadedDateAsync(10).Returns(new List<VideoEntry> { entry });

        var field = typeof(YoutubeService).GetField("_latestChannelDateIndex", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        field.Should().NotBeNull();
        field!.SetValue(_service, new Dictionary<string, DateTime?>
        {
            ["abc123"] = new DateTime(2025, 5, 1, 0, 0, 0, DateTimeKind.Utc)
        });

        var result = await _service.EnrichMissingMetadataAsync(10);

        result.Should().Be(1);
        entry.UploadedDate.Should().NotBeNull();
        await _mockDb.Received(1).UpdateVideoEntryAsync(entry);
    }
}
