using System.Diagnostics;
using FluentAssertions;
using Xunit.Abstractions;
using VideoArchiveManager.Services;

namespace VideoArchiveManager.Tests;

public class MatcherPerformanceBaselineTests
{
    private readonly ITestOutputHelper _output;

    public MatcherPerformanceBaselineTests(ITestOutputHelper output)
    {
        _output = output;
    }

    [Fact]
    public void FindBestMatchYoutubeId_BaselineRun_ShouldCompleteWithinReasonableTime()
    {
        var candidates = Enumerable.Range(1, 10000)
            .Select(i =>
            {
                var title = $"Nyheder fra Fredericia uge {i}";
                return new VideoMatchCandidate(
                    $"id-{i}",
                    title,
                    1200,
                    null,
                    null,
                    VideoTitleMatcher.ExtractSignificantWords(title));
            })
            .ToList();

        long beforeBytes = GC.GetTotalMemory(forceFullCollection: true);
        var sw = Stopwatch.StartNew();

        var matches = 0;
        for (int i = 1; i <= 200; i++)
        {
            var fileName = $"Nyheder fra Fredericia uge {i}.mp4";
            var result = VideoTitleMatcher.FindBestMatchYoutubeId(candidates, fileName, 1200);
            if (!string.IsNullOrWhiteSpace(result))
            {
                matches++;
            }
        }

        sw.Stop();
        long afterBytes = GC.GetTotalMemory(forceFullCollection: true);
        long memoryDeltaBytes = afterBytes - beforeBytes;

        _output.WriteLine($"Baseline matcher run: {sw.ElapsedMilliseconds} ms for 200 lookups over 10,000 candidates.");
        _output.WriteLine($"Baseline matcher memory delta: {memoryDeltaBytes / 1024.0:N0} KB.");
        _output.WriteLine($"Resolved matches: {matches}/200.");

        matches.Should().Be(200);
        sw.ElapsedMilliseconds.Should().BeLessThan(5000);
    }
}