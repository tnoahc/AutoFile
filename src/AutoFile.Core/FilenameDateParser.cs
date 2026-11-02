using System.Globalization;
using System.Text.RegularExpressions;

namespace AutoFile.Core;

public sealed record FilenameDate(DateOnly Date, string Title);

/// <summary>
/// Finds a date already written into a file name (e.g. "2024-03-15 Report", "IMG_20240315_1234",
/// "Minutes 15.03.2024", "240315_Budget") and returns it along with the name minus that date.
/// </summary>
public static partial class FilenameDateParser
{
    const string Separators = " _-.";

    public static FilenameDate? Extract(string stem, DateOnly today)
    {
        // A short YYMMDD prefix with a different separator, e.g. "240315_Budget" or "240315  Budget".
        var shortPrefix = ShortPrefixPattern().Match(stem);
        if (shortPrefix.Success
            && DateOnly.TryParseExact(shortPrefix.Groups["date"].Value, "yyMMdd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var shortDate)
            && IsPlausible(shortDate, today))
        {
            return new FilenameDate(shortDate, RemoveSpan(stem, shortPrefix.Index, shortPrefix.Length));
        }

        foreach (var pattern in new[] { YearFirstPattern(), DayFirstPattern() })
        {
            foreach (Match match in pattern.Matches(stem))
            {
                if (TryBuildDate(match, out var date) && IsPlausible(date, today))
                {
                    return new FilenameDate(date, RemoveSpan(stem, match.Index, match.Length));
                }
            }
        }

        return null;
    }

    static bool TryBuildDate(Match match, out DateOnly date)
    {
        date = default;
        var year = int.Parse(match.Groups["y"].Value);
        var month = int.Parse(match.Groups["m"].Value);
        var day = int.Parse(match.Groups["d"].Value);
        if (month is < 1 or > 12 || day < 1 || day > DateTime.DaysInMonth(year, month))
        {
            return false;
        }

        date = new DateOnly(year, month, day);
        return true;
    }

    static bool IsPlausible(DateOnly date, DateOnly today) =>
        date.Year >= 1970 && date <= today.AddDays(1);

    /// <summary>
    /// Cuts the date out and stitches the remaining pieces back together, reusing the separator
    /// that sat before the date: "Report - 2024-03-15 - final" becomes "Report - final".
    /// </summary>
    static string RemoveSpan(string stem, int index, int length)
    {
        var left = stem[..index];
        var right = stem[(index + length)..];

        var leftTrimmed = left.TrimEnd(Separators.ToCharArray());
        var rightTrimmed = right.TrimStart(Separators.ToCharArray());
        var leftSeparator = left[leftTrimmed.Length..];
        var rightSeparator = right[..(right.Length - rightTrimmed.Length)];

        if (leftTrimmed.Length == 0 || rightTrimmed.Length == 0)
        {
            return (leftTrimmed + rightTrimmed).Trim();
        }

        var joiner = leftSeparator.Length > 0 ? leftSeparator : rightSeparator;
        if (joiner.Length == 0)
        {
            joiner = " ";
        }

        return (leftTrimmed + joiner + rightTrimmed).Trim();
    }

    [GeneratedRegex(@"^(?<date>\d{6})(?=[\s_\-.]|$)")]
    private static partial Regex ShortPrefixPattern();

    // 2024-03-15, 2024_03_15, 2024.03.15, 20240315
    [GeneratedRegex(@"(?<!\d)(?<y>(?:19|20)\d{2})(?<sep>[-_.]?)(?<m>\d{2})\k<sep>(?<d>\d{2})(?!\d)")]
    private static partial Regex YearFirstPattern();

    // 15-03-2024, 15.03.2024, 15_03_2024 (day first; a separator is required so plain numbers aren't mistaken for dates)
    [GeneratedRegex(@"(?<!\d)(?<d>\d{2})(?<sep>[-_.])(?<m>\d{2})\k<sep>(?<y>(?:19|20)\d{2})(?!\d)")]
    private static partial Regex DayFirstPattern();
}
