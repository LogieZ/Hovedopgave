using FluentAssertions;
using VideoArchiveManager.Interfaces;
using VideoArchiveManager.Services;

namespace VideoArchiveManager.Tests;

public class VideoTitleMatcherTests
{
    private readonly IVideoTitleMatcher _matcher = new VideoTitleMatcher();

    [Fact]
    public void FindBestMatchingYoutubeId_WhenFileNameIsEmpty_ShouldReturnNull()
    {
        var result = _matcher.FindBestMatchingYoutubeId(string.Empty, new List<VideoTitleCandidate>());
        result.Should().BeNull();
    }

    [Fact]
    public void FindBestMatchingYoutubeId_WhenCandidatesAreEmpty_ShouldReturnNull()
    {
        var result = _matcher.FindBestMatchingYoutubeId("Nyheder.mp4", new List<VideoTitleCandidate>());
        result.Should().BeNull();
    }

    [Fact]
    public void FindBestMatchingYoutubeId_WhenTitleIsSubstringOfFileName_ShouldMatch()
    {
        var candidates = new List<VideoTitleCandidate>
        {
            new("id-1", "Nyheder fra Fredericia")
        };

        var result = _matcher.FindBestMatchingYoutubeId("Nyheder fra Fredericia mandag.mp4", candidates);

        result.Should().Be("id-1");
    }

    [Fact]
    public void FindBestMatchingYoutubeId_WhenFileNameIsSubstringOfTitle_ShouldMatch()
    {
        var candidates = new List<VideoTitleCandidate>
        {
            new("id-2", "Nyheder fra Fredericia mandag")
        };

        var result = _matcher.FindBestMatchingYoutubeId("Nyheder fra Fredericia.mp4", candidates);

        result.Should().Be("id-2");
    }

    [Fact]
    public void FindBestMatchingYoutubeId_WhenDanishCharactersDifferOnlyByCase_ShouldMatch()
    {
        var candidates = new List<VideoTitleCandidate>
        {
            new("id-da", "Nyheder fra Århus")
        };

        var result = _matcher.FindBestMatchingYoutubeId("nyheder fra århus.mp4", candidates);

        result.Should().Be("id-da");
    }

    [Fact]
    public void FindBestMatchingYoutubeId_WhenTokenOverlapIsHigh_ShouldMatchOnTokenization()
    {
        var candidates = new List<VideoTitleCandidate>
        {
            new("id-token", "Mandag Nyheder Fredericia"),
            new("id-other", "Sport Aalborg")
        };

        var result = _matcher.FindBestMatchingYoutubeId("Fredericia_mandag_nyheder.mp4", candidates);

        result.Should().Be("id-token");
    }

    [Fact]
    public void FindBestMatchingYoutubeId_WhenLevenshteinDistanceIsSeven_ShouldMatch()
    {
        var candidates = new List<VideoTitleCandidate>
        {
            new("id-lev-7", "1234567")
        };

        var result = _matcher.FindBestMatchingYoutubeId("abcdefg.mp4", candidates);

        result.Should().Be("id-lev-7");
    }

    [Fact]
    public void FindBestMatchingYoutubeId_WhenLevenshteinDistanceIsEight_ShouldNotMatch()
    {
        var candidates = new List<VideoTitleCandidate>
        {
            new("id-lev-8", "12345678")
        };

        var result = _matcher.FindBestMatchingYoutubeId("abcdefgh.mp4", candidates);

        result.Should().BeNull();
    }

    [Fact]
    public void FindBestMatchingYoutubeId_WhenTwoCandidatesMatch_ShouldReturnFirst()
    {
        var candidates = new List<VideoTitleCandidate>
        {
            new("id-first", "Nyheder fra Fredericia"),
            new("id-second", "Nyheder fra Fredericia")
        };

        var result = _matcher.FindBestMatchingYoutubeId("Nyheder fra Fredericia.mp4", candidates);

        result.Should().Be("id-first");
    }
}
