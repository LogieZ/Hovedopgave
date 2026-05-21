using System.Globalization;
using System.Text.RegularExpressions;

namespace VideoArchiveManager.Services;

public readonly record struct VideoMatchCandidate(
    string YoutubeId,
    string Title,
    long DurationSeconds,
    DateTime? TitleDate,
    DateTime? UploadedDate,
    IReadOnlySet<string>? SignificantWords = null);

public static class VideoTitleMatcher
{
    private static readonly Dictionary<string, int> DanishMonths = new(StringComparer.OrdinalIgnoreCase)
    {
        { "januar", 1 }, { "februar", 2 }, { "marts", 3 }, { "april", 4 },
        { "maj", 5 }, { "juni", 6 }, { "juli", 7 }, { "august", 8 },
        { "september", 9 }, { "oktober", 10 }, { "november", 11 }, { "december", 12 }
    };

    private static readonly Regex TitleDateRegex = new(
        @"(?:(?:d\.\s*)?(\d{1,2})\.\s*(januar|februar|marts|april|maj|juni|juli|august|september|oktober|november|december)\s+(\d{4}))|(?:(\d{1,2})[-./](\d{1,2})[-./](\d{2,4}))",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex FolderWeekRegex = new(
        @"[Uu]ge\s+(\d{1,2})\s*[-\u2013]\s*(\d{4})",
        RegexOptions.Compiled);

    private static readonly HashSet<string> StopWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "og", "en", "et", "er", "af", "på", "til", "fra", "med", "som", "den", "det",
        "de", "der", "at", "for", "men", "har", "var", "sig", "sin", "sit", "sine",
        "kan", "vil", "hun", "han", "vi", "nu", "da", "så", "om",
        "the", "and", "for", "are", "but", "not", "you", "all", "can", "had",
        "her", "was", "one", "our", "out", "day", "get", "has", "him", "his",
        "how", "man", "new", "now", "old", "see", "two", "way", "who",
        "its", "this", "with", "from", "that",
    };

    public static string? FindBestMatchYoutubeId(
        IEnumerable<VideoMatchCandidate> candidates,
        string fileName,
        long fileDurationSeconds = 0,
        string filePath = "")
    {
        var fileNameOnly = Path.GetFileNameWithoutExtension(fileName);
        var cleanTitleFragment = Regex.Replace(fileNameOnly, @"^[0-9-]+", "").Trim();
        var fileWeek = ExtractWeekFromPath(filePath);
        var candidateList = candidates.ToList();

        var simpleMatch = candidateList.FirstOrDefault(v =>
        {
            if (v.Title.Length < 5 || cleanTitleFragment.Length < 5) return false;

            int dbSig = CountSignificantWords(v.Title);
            int fileSig = CountSignificantWords(cleanTitleFragment);

            if (dbSig == 0 || fileSig == 0) return false;

            if (cleanTitleFragment.Contains(v.Title, StringComparison.OrdinalIgnoreCase)
                && DurationMatches(v.DurationSeconds, fileDurationSeconds)
                && WeekMatches(v.TitleDate, v.UploadedDate, fileWeek))
            {
                return (double)dbSig / fileSig >= 0.6;
            }

            if (v.Title.Contains(cleanTitleFragment, StringComparison.OrdinalIgnoreCase)
                && DurationMatches(v.DurationSeconds, fileDurationSeconds)
                && WeekMatches(v.TitleDate, v.UploadedDate, fileWeek))
            {
                return (double)fileSig / dbSig >= 0.6;
            }

            return false;
        });

        if (!string.IsNullOrWhiteSpace(simpleMatch.YoutubeId))
        {
            return simpleMatch.YoutubeId;
        }

        var fileWords = ExtractSignificantWords(cleanTitleFragment);

        if (fileWords.Count == 0)
        {
            return null;
        }

        var tokenRanked = candidateList.Select(v =>
        {
            var titleWords = v.SignificantWords ?? ExtractSignificantWords(v.Title);

            int score = titleWords.Intersect(fileWords).Count();
            double coverage = (double)score / fileWords.Count;

            return new { Entry = v, Score = score, Coverage = coverage };
        })
        .ToList();

        var bestTokenMatch = tokenRanked
            .Where(x => x.Score >= 2 && x.Coverage >= 0.4)
            .OrderByDescending(x => x.Score)
            .ThenByDescending(x => x.Coverage)
            .FirstOrDefault();

        if (bestTokenMatch != null
            && DurationMatches(bestTokenMatch.Entry.DurationSeconds, fileDurationSeconds)
            && WeekMatches(bestTokenMatch.Entry.TitleDate, bestTokenMatch.Entry.UploadedDate, fileWeek))
        {
            return bestTokenMatch.Entry.YoutubeId;
        }

        // Low-signal short-circuit: if there is no meaningful token overlap at all,
        // Levenshtein across the full candidate set is usually expensive noise.
        int maxTokenScore = tokenRanked.Count == 0 ? 0 : tokenRanked.Max(x => x.Score);
        if (fileWords.Count >= 3 && maxTokenScore == 0)
        {
            return null;
        }

        // Reduce CPU by limiting Levenshtein to titles with comparable length.
        int fileLength = cleanTitleFragment.Length;
        var fuzzyCandidates = candidateList
            .Where(v => Math.Abs(v.Title.Length - fileLength) <= 12)
            .ToList();

        if (fuzzyCandidates.Count == 0)
        {
            return null;
        }

        var fuzzyMatch = fuzzyCandidates
            .Select(v => new { Entry = v, Distance = CalculateLevenshteinDistance(cleanTitleFragment.ToLower(), v.Title.ToLower()) })
            .OrderBy(x => x.Distance)
            .FirstOrDefault();

        if (fuzzyMatch != null
            && fuzzyMatch.Distance < 8
            && DurationMatches(fuzzyMatch.Entry.DurationSeconds, fileDurationSeconds)
            && WeekMatches(fuzzyMatch.Entry.TitleDate, fuzzyMatch.Entry.UploadedDate, fileWeek))
        {
            return fuzzyMatch.Entry.YoutubeId;
        }

        return null;
    }

    public static DateTime? ExtractDateFromTitle(string title)
    {
        var match = TitleDateRegex.Match(title);
        if (!match.Success) return null;

        int day;
        int month;
        int year;

        if (!string.IsNullOrEmpty(match.Groups[1].Value))
        {
            if (!int.TryParse(match.Groups[1].Value, out day)) return null;
            if (!DanishMonths.TryGetValue(match.Groups[2].Value, out month)) return null;
            if (!int.TryParse(match.Groups[3].Value, out year)) return null;
        }
        else if (!string.IsNullOrEmpty(match.Groups[4].Value))
        {
            if (!int.TryParse(match.Groups[4].Value, out int num1)) return null;
            if (!int.TryParse(match.Groups[5].Value, out int num2)) return null;
            if (!int.TryParse(match.Groups[6].Value, out int num3)) return null;

            int expandedYear = num3 < 100 ? (num3 < 51 ? 2000 + num3 : 1900 + num3) : num3;

            if (num1 >= 1 && num1 <= 31 && num2 >= 1 && num2 <= 12)
            {
                day = num1;
                month = num2;
                year = expandedYear;
            }
            else if (num2 >= 1 && num2 <= 12 && num3 >= 1 && num3 <= 31)
            {
                year = num1 < 100 ? (num1 < 51 ? 2000 + num1 : 1900 + num1) : num1;
                month = num2;
                day = num3;
            }
            else
            {
                return null;
            }
        }
        else
        {
            return null;
        }

        try
        {
            return new DateTime(year, month, day, 0, 0, 0, DateTimeKind.Utc);
        }
        catch
        {
            return null;
        }
    }

    public static HashSet<string> ExtractSignificantWords(string text)
    {
        return text.ToLower()
            .Split(new[] { ' ', '-', '_' }, StringSplitOptions.RemoveEmptyEntries)
            .Where(w => w.Length >= 3 && !StopWords.Contains(w))
            .ToHashSet();
    }

    private static int CountSignificantWords(string text) =>
        ExtractSignificantWords(text).Count;

    private static bool DurationMatches(long candidateDurationSeconds, long fileDurationSeconds)
    {
        if (fileDurationSeconds <= 0 || candidateDurationSeconds <= 0)
        {
            return true;
        }

        return Math.Abs(candidateDurationSeconds - fileDurationSeconds) <= 10;
    }

    private static bool WeekMatches(DateTime? titleDate, DateTime? uploadedDate, (int Week, int Year)? fileWeek)
    {
        if (fileWeek == null)
        {
            return true;
        }

        var referenceDate = titleDate ?? uploadedDate;
        if (referenceDate == null)
        {
            return true;
        }

        int referenceWeek = ISOWeek.GetWeekOfYear(referenceDate.Value);
        int referenceYear = ISOWeek.GetYear(referenceDate.Value);

        if (referenceYear != fileWeek.Value.Year)
        {
            return false;
        }

        return referenceWeek == fileWeek.Value.Week;
    }

    private static (int Week, int Year)? ExtractWeekFromPath(string filePath)
    {
        if (string.IsNullOrEmpty(filePath)) return null;

        var match = FolderWeekRegex.Match(filePath);
        if (!match.Success) return null;

        if (!int.TryParse(match.Groups[1].Value, out int week)) return null;
        if (!int.TryParse(match.Groups[2].Value, out int year)) return null;

        return (week, year);
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
                int cost = target[j - 1] == source[i - 1] ? 0 : 1;
                distance[i, j] = Math.Min(
                    Math.Min(distance[i - 1, j] + 1, distance[i, j - 1] + 1),
                    distance[i - 1, j - 1] + cost);
            }
        }

        return distance[source.Length, target.Length];
    }
}