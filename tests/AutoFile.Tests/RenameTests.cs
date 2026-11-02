using AutoFile.Core;

namespace AutoFile.Tests;

public sealed class RenameTests : IDisposable
{
    readonly string root = Path.Combine(Path.GetTempPath(), "AutoFileTests", Guid.NewGuid().ToString("N"));
    readonly string folder;
    readonly RenameJournal journal;

    public RenameTests()
    {
        folder = Path.Combine(root, "files");
        Directory.CreateDirectory(folder);
        journal = new RenameJournal(Path.Combine(root, "journal"));
    }

    string CreateFile(string name, DateTime? written = null)
    {
        var path = Path.Combine(folder, name);
        File.WriteAllText(path, "x");
        if (written is { } when)
        {
            File.SetCreationTime(path, when);
            File.SetLastWriteTime(path, when);
        }

        return path;
    }

    [Fact]
    public void PlansOnlyNonConformingFiles()
    {
        CreateFile("Budget Review.txt", new DateTime(2025, 6, 1));
        CreateFile("251010 Already fine.txt");
        CreateFile("~$lock.docx");
        CreateFile("desktop.ini");

        var plans = new RenamePlanner(new AutoFileConfig()).PlanFolder(folder, recursive: false);

        var plan = Assert.Single(plans);
        Assert.Equal("Budget Review.txt", plan.SourceName);
        Assert.Equal("250601 Budget Review.txt", plan.TargetName);
        Assert.Equal(DateSource.FileSystem, plan.Source);
    }

    [Fact]
    public void UsesEarlierOfCreatedAndModified()
    {
        var path = CreateFile("Copied.txt");
        File.SetLastWriteTime(path, new DateTime(2023, 1, 9));
        File.SetCreationTime(path, new DateTime(2026, 10, 30)); // as if just copied

        var plan = Assert.Single(new RenamePlanner(new AutoFileConfig()).PlanFolder(folder, false));
        Assert.Equal("230109 Copied.txt", plan.TargetName);
    }

    [Fact]
    public void AvoidsNameCollisions()
    {
        CreateFile("240315 Report.txt");
        CreateFile("2024-03-15 Report.txt");
        CreateFile("Report 15.03.2024.txt");

        var targets = new RenamePlanner(new AutoFileConfig()).PlanFolder(folder, false)
            .Select(p => p.TargetName).Order().ToList();

        Assert.Equal(["240315 Report (2).txt", "240315 Report (3).txt"], targets);
    }

    [Fact]
    public void RecursiveScanIncludesSubfolders()
    {
        Directory.CreateDirectory(Path.Combine(folder, "sub"));
        File.WriteAllText(Path.Combine(folder, "sub", "2024-01-02 Nested.txt"), "x");

        Assert.Empty(new RenamePlanner(new AutoFileConfig()).PlanFolder(folder, recursive: false));
        var plan = Assert.Single(new RenamePlanner(new AutoFileConfig()).PlanFolder(folder, recursive: true));
        Assert.Equal("240102 Nested.txt", plan.TargetName);
    }

    [Fact]
    public void ConvertsNamesFromAnotherConvention()
    {
        CreateFile("240315 Report.txt");
        CreateFile("2024-03-15 Already right.txt");
        CreateFile("Budget Review.txt", new DateTime(2025, 6, 1));

        var planner = new RenamePlanner(new AutoFileConfig(), convention: NamingConvention.Parse("YYYY-MM-DD Title"));
        var targets = planner.PlanFolder(folder, false).ToDictionary(p => p.SourceName, p => p.TargetName);

        Assert.Equal(2, targets.Count);
        Assert.Equal("2024-03-15 Report.txt", targets["240315 Report.txt"]);
        Assert.Equal("2025-06-01 Budget Review.txt", targets["Budget Review.txt"]);
    }

    [Fact]
    public void ConvertsFromACustomConventionTheConfigKnowsAbout()
    {
        CreateFile("Report (15.03.2024).txt");
        var config = new AutoFileConfig
        {
            WatchedFolders = [new WatchedFolder { Path = Path.Combine(root, "elsewhere"), Convention = "Title (DD.MM.YYYY)" }],
        };

        var plan = Assert.Single(new RenamePlanner(config).PlanFolder(folder, false));
        Assert.Equal("240315 Report.txt", plan.TargetName);
    }

    [Fact]
    public void WatchedFolderConventionOverridesDefault()
    {
        var sub = Path.Combine(folder, "photos");
        Directory.CreateDirectory(sub);
        File.WriteAllText(Path.Combine(sub, "IMG_20240315.jpg"), "x");
        CreateFile("Notes 2024-03-15.txt");
        var config = new AutoFileConfig
        {
            Convention = "YYMMDD Title",
            WatchedFolders = [new WatchedFolder { Path = sub, Convention = "YYYY-MM-DD Title" }],
        };

        var targets = new RenamePlanner(config).PlanFolder(folder, recursive: true).Select(p => p.TargetName).Order().ToList();

        Assert.Equal(["2024-03-15 IMG.jpg", "240315 Notes.txt"], targets);
    }

    [Fact]
    public void RecordsBatchInGitAndUndoesIt()
    {
        var original = CreateFile("2024-03-15 Notes.txt");
        var plans = new RenamePlanner(new AutoFileConfig()).PlanFolder(folder, false);

        var (results, batch) = RenameExecutor.ApplyAndRecord(plans, journal, "scan");

        Assert.All(results, r => Assert.True(r.Succeeded));
        Assert.NotNull(batch);
        Assert.True(File.Exists(Path.Combine(folder, "240315 Notes.txt")));
        var commit = Assert.Single(journal.History(10));
        Assert.Equal(batch.Id, commit.BatchId);
        Assert.Equal("scan: renamed 1 file", commit.Summary);

        var outcome = journal.Undo();

        Assert.Empty(outcome.Problems);
        Assert.True(File.Exists(original));
        Assert.Equal(2, journal.History(10).Count);
        Assert.Contains(original, journal.RestoredPaths());
        Assert.Throws<InvalidOperationException>(() => journal.Undo());
    }

    [Fact]
    public void UndoReportsFilesThatMovedOn()
    {
        CreateFile("2024-03-15 Gone.txt");
        var plans = new RenamePlanner(new AutoFileConfig()).PlanFolder(folder, false);
        var (_, batch) = RenameExecutor.ApplyAndRecord(plans, journal, "scan");
        File.Delete(Path.Combine(folder, "240315 Gone.txt"));

        var outcome = journal.Undo(batch!.Id[..12]);

        Assert.Null(outcome.UndoBatch);
        Assert.Single(outcome.Problems);
    }

    public void Dispose()
    {
        // libgit2 marks object files read-only; clear that so the temp folder can be deleted.
        foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
        {
            File.SetAttributes(file, FileAttributes.Normal);
        }

        Directory.Delete(root, recursive: true);
    }
}
