using System.Text.RegularExpressions;
using VideoArchiveManager.Interfaces;

namespace VideoArchiveManager.Services;

public sealed class VideoTitleMatcher : IVideoTitleMatcher
{
    public string? FindBestMatchingYoutubeId(string fileName, IReadOnlyList<VideoTitleCandidate> candidates)
    {
        if (string.IsNullOrWhiteSpace(fileName) || candidates.Count == 0)
        {
            return null;
        }

        var fileNameOnly = Path.GetFileNameWithoutExtension(fileName);
        var cleanTitleFragment = Regex.Replace(fileNameOnly, @"^[0-9-]+", "").Trim();

        if (string.IsNullOrWhiteSpace(cleanTitleFragment))
        {
            return null;
        }

        var simpleMatch = candidates.FirstOrDefault(v =>
            v.Title.Contains(cleanTitleFragment, StringComparison.OrdinalIgnoreCase) ||
            cleanTitleFragment.Contains(v.Title, StringComparison.OrdinalIgnoreCase));

        if (simpleMatch != null)
        {
            return simpleMatch.YoutubeId;
        }

        var fileWords = cleanTitleFragment.ToLowerInvariant()
            .Split(new[] { ' ', '-', '_' }, StringSplitOptions.RemoveEmptyEntries)
            .Where(w => w.Length >= 2)
            .ToList();

        var bestTokenMatch = candidates
            .Select(v => new
            {
                Entry = v,
                Score = v.Title.ToLowerInvariant().Split(' ').Intersect(fileWords).Count()
            })
            .Where(x => x.Score >= 2)
            .OrderByDescending(x => x.Score)
            .FirstOrDefault();

        if (bestTokenMatch != null)
        {
            return bestTokenMatch.Entry.YoutubeId;
        }

        var fuzzyMatch = candidates
            .Select(v => new
            {
                Entry = v,
                Distance = CalculateLevenshteinDistance(cleanTitleFragment.ToLowerInvariant(), v.Title.ToLowerInvariant())
            })
            .OrderBy(x => x.Distance)
            .FirstOrDefault();

        return fuzzyMatch != null && fuzzyMatch.Distance < 8
            ? fuzzyMatch.Entry.YoutubeId
            : null;
    }

    private static int CalculateLevenshteinDistance(string source, string target)
    {
        if (string.IsNullOrEmpty(source)) return target.Length;
        if (string.IsNullOrEmpty(target)) return source.Length;

        var distance = new int[source.Length + 1, target.Length + 1];

        for (int i = 0; i <= source.Length; i++) distance[i, 0] = i;
        for (int j = 0; j <= target.Length; j++) distance[0, j] = j;

        for (int i = 1; i <= source.Length; i++)
        {
            for (int j = 1; j <= target.Length; j++)
            {
                var cost = target[j - 1] == source[i - 1] ? 0 : 1;
                distance[i, j] = Math.Min(
                    Math.Min(distance[i - 1, j] + 1, distance[i, j - 1] + 1),
                    distance[i - 1, j - 1] + cost);
            }
        }

        return distance[source.Length, target.Length];
    }
}
