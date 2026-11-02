using AutoFile.Core;

namespace AutoFile.Tests;

public sealed class WatcherTests : IDisposable
{
    readonly string root = Path.Combine(Path.GetTempPath(), "AutoFileTests", Guid.NewGuid().ToString("N"));
    readonly string folder;

    public WatcherTests()
    {
        folder = Path.Combine(root, "files");
        Directory.CreateDirectory(folder);
    }

    [Fact]
    public async Task RenamesNewFilesAndRespectsUndo()
    {
        var journal = new RenameJournal(Path.Combine(root, "journal"));
        var config = new AutoFileConfig
        {
            StableSeconds = 1,
            WatchedFolders = [new WatchedFolder { Path = folder }],
        };
        using var watcher = new FolderWatcher(config, journal);
        var renamed = new TaskCompletionSource<RenameBatch>(TaskCreationOptions.RunContinuationsAsynchronously);
        watcher.BatchApplied += batch => renamed.TrySetResult(batch);
        watcher.Start();

        var original = Path.Combine(folder, "2024-03-15 Dropped in.txt");
        await File.WriteAllTextAsync(original, "x");

        var batch = await renamed.Task.WaitAsync(TimeSpan.FromSeconds(15));
        Assert.Equal(Path.Combine(folder, "240315 Dropped in.txt"), Assert.Single(batch.Entries).NewPath);

        journal.Undo();
        await Task.Delay(TimeSpan.FromSeconds(4));
        watcher.SweepSoon();
        await Task.Delay(TimeSpan.FromSeconds(2));

        Assert.True(File.Exists(original), "an undone file should keep its restored name");
    }

    [Fact]
    public async Task RetriesFilesThatWereStillFreshAtStartup()
    {
        // Written just before the watcher starts, so no filesystem event will ever arrive for it
        // and the first sweep sees it as too new to touch.
        await File.WriteAllTextAsync(Path.Combine(folder, "2024-01-02 Early bird.txt"), "x");

        var config = new AutoFileConfig
        {
            StableSeconds = 2,
            RescanMinutes = 60,
            WatchedFolders = [new WatchedFolder { Path = folder }],
        };
        using var watcher = new FolderWatcher(config, new RenameJournal(Path.Combine(root, "journal")));
        var renamed = new TaskCompletionSource<RenameBatch>(TaskCreationOptions.RunContinuationsAsynchronously);
        watcher.BatchApplied += batch => renamed.TrySetResult(batch);
        watcher.Start();

        var batch = await renamed.Task.WaitAsync(TimeSpan.FromSeconds(15));
        Assert.EndsWith("240102 Early bird.txt", Assert.Single(batch.Entries).NewPath);
    }

    public void Dispose()
    {
        foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
        {
            File.SetAttributes(file, FileAttributes.Normal);
        }

        Directory.Delete(root, recursive: true);
    }
}
