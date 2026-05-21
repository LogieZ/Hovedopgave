using FluentAssertions;
using VideoArchiveManager.Services;

namespace VideoArchiveManager.Tests;

public class VideoTitleMatcherTests
{
    [Fact]
    public void FindBestMatchYoutubeId_WhenExactTitleMatch_ShouldReturnCandidateId()
    {
        var candidates = new List<VideoMatchCandidate>
        {
            new("id-1", "Nyheder fra Fredericia mandag", 1200, null, null),
            new("id-2", "Andet program", 900, null, null)
        };

        var result = VideoTitleMatcher.FindBestMatchYoutubeId(candidates, "Nyheder fra Fredericia mandag.mp4");

        result.Should().Be("id-1");
    }

    [Fact]
    public void FindBestMatchYoutubeId_WhenDurationDiffTooLarge_ShouldRejectOtherwiseGoodMatch()
    {
        var candidates = new List<VideoMatchCandidate>
        {
            new("id-1", "Nyheder fra Fredericia mandag", 1200, null, null)
        };

        var result = VideoTitleMatcher.FindBestMatchYoutubeId(
            candidates,
            "Nyheder fra Fredericia mandag.mp4",
            fileDurationSeconds: 800);

        result.Should().BeNull();
    }

    [Fact]
    public void ExtractDateFromTitle_WhenDanishDateExists_ShouldParseUtcDate()
    {
        var parsed = VideoTitleMatcher.ExtractDateFromTitle("Ugeavis d. 8. november 2025");

        parsed.Should().Be(new DateTime(2025, 11, 8, 0, 0, 0, DateTimeKind.Utc));
    }
}
