namespace VideoArchiveManager.Interfaces;

public interface IVideoTitleMatcher
{
    string? FindBestMatchingYoutubeId(string fileName, IReadOnlyList<VideoTitleCandidate> candidates);
}

public sealed record VideoTitleCandidate(string YoutubeId, string Title);
