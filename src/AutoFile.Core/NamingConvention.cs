using System.Collections.Concurrent;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace AutoFile.Core;

/// <summary>
/// A file naming convention written the way people describe it: a date made of YYYY or YY, MM and DD
/// (optionally separated by - _ . or a space), the word Title, and any fixed text around them.
/// Examples: "YYMMDD Title", "YYYY-MM-DD - Title", "Title_YYYYMMDD".
/// </summary>
public sealed partial class NamingConvention
{
    public const string DefaultPattern = "YYMMDD Title";

    public const string UntitledTitle = "Untitled";

    public static IReadOnlyList<string> Presets { get; } =
    [
        "YYMMDD Title",
        "YYYY-MM-DD Title",
        "YYYYMMDD Title",
        "YYYYMMDD_Title",
        "YYYY-MM-DD - Title",
        "DD-MM-YYYY Title",
        "Title - YYYY-MM-DD",
        "Title_YYYYMMDD",
    ];

    // Declared before Default: static initialisers run in order, and Parse needs the cache.
    static readonly ConcurrentDictionary<string, NamingConvention> Cache = new(StringComparer.Ordinal);

    public static NamingConvention Default { get; } = Parse(DefaultPattern);

    readonly string before;
    readonly string between;
    readonly string after;
    readonly bool titleFirst;
    readonly string dateFormat;
    readonly Regex matcher;

    NamingConvention(string pattern, string before, string between, string after, bool titleFirst, string dateFormat, Regex matcher)
    {
        Pattern = pattern;
        this.before = before;
        this.between = between;
        this.after = after;
        this.titleFirst = titleFirst;
        this.dateFormat = dateFormat;
        this.matcher = matcher;
    }

    public string Pattern { get; }

    /// <summary>Parses a convention, throwing <see cref="FormatException"/> with a message fit to show the user.</summary>
    public static NamingConvention Parse(string pattern) =>
        TryParse(pattern, out var convention, out var error) ? convention : throw new FormatException(error);

    public static bool TryParse(string? pattern, out NamingConvention convention, out string error)
    {
        pattern = pattern?.Trim() ?? "";
        if (Cache.TryGetValue(pattern, out convention!))
        {
            error = "";
            return true;
        }

        error = Validate(pattern, out var dateBlock, out var title);
        if (error.Length > 0)
        {
            convention = null!;
            return false;
        }

        var titleFirst = title.Index < dateBlock.Index;
        var (first, second) = titleFirst ? (title, dateBlock) : (dateBlock, title);
        var before = pattern[..first.Index];
        var between = pattern[(first.Index + first.Length)..second.Index];
        var after = pattern[(second.Index + second.Length)..];

        var dateFormat = DateTokenPattern().Replace(dateBlock.Value, m => m.Value switch
        {
            "YYYY" => "yyyy",
            "YY" => "yy",
            "DD" => "dd",
            _ => "MM",
        });

        var dateRegex = new StringBuilder();
        var last = 0;
        foreach (Match token in DateTokenPattern().Matches(dateBlock.Value))
        {
            dateRegex.Append(Regex.Escape(dateBlock.Value[last..token.Index]));
            dateRegex.Append(token.Value == "YYYY" ? @"\d{4}" : @"\d{2}");
            last = token.Index + token.Length;
        }

        const string titleRegex = @"(?<title>\S(?:.*\S)?)";
        var firstRegex = titleFirst ? titleRegex : $"(?<date>{dateRegex})";
        var secondRegex = titleFirst ? $"(?<date>{dateRegex})" : titleRegex;
        var matcher = new Regex(
            $"^{Regex.Escape(before)}{firstRegex}{Regex.Escape(between)}{secondRegex}{Regex.Escape(after)}$",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        convention = Cache.GetOrAdd(pattern, new NamingConvention(pattern, before, between, after, titleFirst, dateFormat, matcher));
        return true;
    }

    static string Validate(string pattern, out Match dateBlock, out Match title)
    {
        dateBlock = Match.Empty;
        title = Match.Empty;

        if (pattern.Length == 0)
        {
            return "Enter a convention, for example YYMMDD Title.";
        }

        if (pattern.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
        {
            return "File names can't contain \\ / : * ? \" < > |";
        }

        var titles = TitlePattern().Matches(pattern);
        if (titles.Count != 1)
        {
            return "Include the word Title once, where the document's name should go.";
        }

        var dates = DateBlockPattern().Matches(pattern);
        if (dates.Count != 1)
        {
            return "Include the date once, for example YYMMDD or YYYY-MM-DD.";
        }

        var tokens = DateTokenPattern().Matches(dates[0].Value).Select(m => m.Value).ToList();
        if (tokens.Count(t => t is "YYYY" or "YY") != 1 || tokens.Count(t => t == "MM") != 1 || tokens.Count(t => t == "DD") != 1)
        {
            return "The date needs a year (YYYY or YY), a month (MM) and a day (DD).";
        }

        dateBlock = dates[0];
        title = titles[0];
        var gapStart = Math.Min(dateBlock.Index + dateBlock.Length, title.Index + title.Length);
        var gapEnd = Math.Max(dateBlock.Index, title.Index);
        if (gapEnd <= gapStart)
        {
            return "Put something between the date and Title, such as a space, - or _.";
        }

        return "";
    }

    public string Format(DateOnly date, string title)
    {
        var cleanTitle = string.IsNullOrWhiteSpace(title) ? UntitledTitle : title.Trim();
        var dateText = date.ToString(dateFormat, CultureInfo.InvariantCulture);
        return titleFirst
            ? before + cleanTitle + between + dateText + after
            : before + dateText + between + cleanTitle + after;
    }

    /// <summary>The full file name for a title and extension, e.g. "251102 Budget Review.docx".</summary>
    public string FileName(DateOnly date, string title, string extension) => Format(date, title) + extension;

    public string Example(DateOnly date) => FileName(date, "Budget Review", ".docx");

    /// <summary>True when the name (without extension) already follows this convention with a real date.</summary>
    public bool IsConforming(string stem) => TryRead(stem, out _, out _);

    /// <summary>
    /// Reads the date and title out of a name that follows this convention. Used to convert names from
    /// one convention to another, so dates in the future or before 1970 are rejected as unlikely.
    /// </summary>
    public bool TryMatch(string stem, DateOnly today, out DateOnly date, out string title) =>
        TryRead(stem, out date, out title) && date.Year >= 1970 && date <= today.AddDays(1);

    bool TryRead(string stem, out DateOnly date, out string title)
    {
        date = default;
        title = "";
        var match = matcher.Match(stem);
        if (!match.Success
            || !DateOnly.TryParseExact(match.Groups["date"].Value, dateFormat, CultureInfo.InvariantCulture, DateTimeStyles.None, out date))
        {
            return false;
        }

        title = match.Groups["title"].Value;
        return true;
    }

    public override string ToString() => Pattern;

    [GeneratedRegex("title", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex TitlePattern();

    [GeneratedRegex(@"(?:YYYY|YY|MM|DD)(?:[-_. ]?(?:YYYY|YY|MM|DD))*")]
    private static partial Regex DateBlockPattern();

    [GeneratedRegex("YYYY|YY|MM|DD")]
    private static partial Regex DateTokenPattern();
}
