using AutoFile.Core;

namespace AutoFile.Tests;

public class NamingTests
{
    static readonly DateOnly Today = new(2026, 11, 2);

    [Theory]
    [InlineData("240315 Report", true)]
    [InlineData("261102 Budget Review", true)]
    [InlineData("240315_Report", false)]
    [InlineData("240315Report", false)]
    [InlineData("241315 Bad month", false)]
    [InlineData("240315 ", false)]
    [InlineData("Report", false)]
    public void DefaultConventionConformity(string stem, bool expected) =>
        Assert.Equal(expected, NamingConvention.Default.IsConforming(stem));

    [Theory]
    [InlineData("2024-03-15 Meeting notes", "2024-03-15", "Meeting notes")]
    [InlineData("Report_2024_03_15", "2024-03-15", "Report")]
    [InlineData("IMG_20240315_123456", "2024-03-15", "IMG_123456")]
    [InlineData("Report - 2024-03-15 - final", "2024-03-15", "Report - final")]
    [InlineData("Minutes 15.03.2024", "2024-03-15", "Minutes")]
    [InlineData("240315_Plan", "2024-03-15", "Plan")]
    [InlineData("240315  Double space", "2024-03-15", "Double space")]
    [InlineData("20240315", "2024-03-15", "")]
    public void ExtractsDateFromName(string stem, string date, string title)
    {
        var found = FilenameDateParser.Extract(stem, Today);
        Assert.NotNull(found);
        Assert.Equal(DateOnly.Parse(date), found.Date);
        Assert.Equal(title, found.Title);
    }

    [Theory]
    [InlineData("Invoice 12345678")]       // not a valid date
    [InlineData("Plan for 2099-01-01")]    // in the future
    [InlineData("Version 1.2.3")]
    [InlineData("Budget Review")]
    public void IgnoresThingsThatAreNotDates(string stem) =>
        Assert.Null(FilenameDateParser.Extract(stem, Today));

    [Fact]
    public void EmptyTitleBecomesUntitled() =>
        Assert.Equal("240315 Untitled.pdf", NamingConvention.Default.FileName(new DateOnly(2024, 3, 15), "", ".pdf"));

    [Theory]
    [InlineData("YYMMDD Title", "240315 Budget Review")]
    [InlineData("YYYY-MM-DD Title", "2024-03-15 Budget Review")]
    [InlineData("YYYYMMDD_Title", "20240315_Budget Review")]
    [InlineData("YYYY-MM-DD - Title", "2024-03-15 - Budget Review")]
    [InlineData("DD-MM-YYYY Title", "15-03-2024 Budget Review")]
    [InlineData("Title - YYYY-MM-DD", "Budget Review - 2024-03-15")]
    [InlineData("Title - DD.MM.YYYY", "Budget Review - 15.03.2024")]
    [InlineData("Title_YYYYMMDD", "Budget Review_20240315")]
    [InlineData("[YYYY.MM.DD] Title", "[2024.03.15] Budget Review")]
    public void FormatsAndRecognisesItsOwnNames(string pattern, string expected)
    {
        var convention = NamingConvention.Parse(pattern);
        var name = convention.Format(new DateOnly(2024, 3, 15), "Budget Review");

        Assert.Equal(expected, name);
        Assert.True(convention.IsConforming(name));
        Assert.True(convention.TryMatch(name, Today, out var date, out var title));
        Assert.Equal(new DateOnly(2024, 3, 15), date);
        Assert.Equal("Budget Review", title);
    }

    [Theory]
    [InlineData("", "Enter a convention")]
    [InlineData("YYMMDD", "Title")]
    [InlineData("Title", "date")]
    [InlineData("YYMMDD Title YYMMDD", "date once")]
    [InlineData("YYMM Title", "a year")]
    [InlineData("YYMMDDTitle", "between")]
    [InlineData("YYMMDD: Title", "can't contain")]
    public void RejectsInvalidConventions(string pattern, string messagePart)
    {
        Assert.False(NamingConvention.TryParse(pattern, out _, out var error));
        Assert.Contains(messagePart, error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void TitleFirstConventionKeepsSeparatorsInsideTitle()
    {
        var convention = NamingConvention.Parse("Title - YYYY-MM-DD");
        Assert.True(convention.TryMatch("Q1 - Draft - 2024-03-15", Today, out _, out var title));
        Assert.Equal("Q1 - Draft", title);
    }
}
