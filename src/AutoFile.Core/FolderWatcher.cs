using System.Collections.Concurrent;

namespace AutoFile.Core;

/// <summary>
/// Watches the configured folders and renames new or changed files once they have settled
/// (no writes for <see cref="AutoFileConfig.StableSeconds"/> and not open in another program).
/// A periodic full sweep catches anything the filesystem events missed.
/// </summary>
public sealed class FolderWatcher : IDisposable
{
    readonly RenameJournal journal;
    readonly string[] excludedDirectories;
    readonly List<FileSystemWatcher> watchers = [];
    readonly ConcurrentDictionary<string, DateTime> pending = new(StringComparer.OrdinalIgnoreCase);
    readonly object processingGate = new();
    AutoFileConfig config;
    RenamePlanner planner;
    Timer? timer;
    DateTime nextSweep = DateTime.MinValue;

    public FolderWatcher(AutoFileConfig config, RenameJournal journal, IEnumerable<string>? excludedDirectories = null)
    {
        this.config = config;
        this.journal = journal;
        this.excludedDirectories = [.. excludedDirectories ?? []];
        planner = new RenamePlanner(config, this.excludedDirectories);
    }

    /// <summary>Raised (on a background thread) after a batch of files has been renamed.</summary>
    public event Action<RenameBatch>? BatchApplied;

    /// <summary>Raised (on a background thread) with progress and problems worth showing the user.</summary>
    public event Action<string>? Message;

    public bool Paused { get; set; }

    public void Start()
    {
        CreateWatchers();
        timer = new Timer(_ => Tick(), null, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1));
    }

    public void Reconfigure(AutoFileConfig newConfig)
    {
        lock (processingGate)
        {
            config = newConfig;
            planner = new RenamePlanner(newConfig, excludedDirectories);
            DisposeWatchers();
            CreateWatchers();
            nextSweep = DateTime.MinValue;
        }
    }

    /// <summary>Asks for a full sweep of every watched folder on the next tick.</summary>
    public void SweepSoon() => nextSweep = DateTime.MinValue;

    void CreateWatchers()
    {
        foreach (var folder in config.WatchedFolders)
        {
            if (!Directory.Exists(folder.Path))
            {
                Message?.Invoke($"Watched folder not found, skipping: {folder.Path}");
                continue;
            }

            var watcher = new FileSystemWatcher(folder.Path)
            {
                IncludeSubdirectories = folder.Recursive,
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.Size,
                InternalBufferSize = 64 * 1024,
            };
            watcher.Created += (_, e) => Touch(e.FullPath);
            watcher.Changed += (_, e) => Touch(e.FullPath);
            watcher.Renamed += (_, e) => Touch(e.FullPath);
            watcher.Error += (_, e) =>
            {
                Message?.Invoke($"Lost track of changes in {folder.Path} ({e.GetException().Message}); doing a full sweep.");
                SweepSoon();
            };
            watcher.EnableRaisingEvents = true;
            watchers.Add(watcher);
        }
    }

    void Touch(string path) => pending[path] = DateTime.UtcNow;

    void Tick()
    {
        if (Paused || !Monitor.TryEnter(processingGate))
        {
            return;
        }

        try
        {
            if (DateTime.UtcNow >= nextSweep)
            {
                nextSweep = DateTime.UtcNow.AddMinutes(Math.Max(1, config.RescanMinutes));
                Sweep();
            }

            ProcessPending();
        }
        catch (Exception ex)
        {
            Message?.Invoke($"Watcher error: {ex.Message}");
        }
        finally
        {
            Monitor.Exit(processingGate);
        }
    }

    void Sweep()
    {
        var settledBefore = DateTime.Now.AddSeconds(-config.StableSeconds);
        var ready = new List<PlannedRename>();
        foreach (var plan in config.WatchedFolders
                     .Where(f => Directory.Exists(f.Path))
                     .SelectMany(f => planner.PlanFolder(f.Path, f.Recursive)))
        {
            if (File.GetLastWriteTime(plan.SourcePath) < settledBefore && RenameExecutor.IsReady(plan.SourcePath))
            {
                ready.Add(plan);
            }
            else
            {
                // Still being written or open elsewhere: hand it to the pending queue to retry shortly.
                Touch(plan.SourcePath);
            }
        }

        Apply(ready);
    }

    void ProcessPending()
    {
        var settledBefore = DateTime.UtcNow.AddSeconds(-config.StableSeconds);
        var ready = new List<string>();
        foreach (var (path, lastSeen) in pending)
        {
            if (lastSeen > settledBefore || !pending.TryRemove(new KeyValuePair<string, DateTime>(path, lastSeen)))
            {
                continue;
            }

            if (!File.Exists(path))
            {
                continue;
            }

            if (RenameExecutor.IsReady(path))
            {
                ready.Add(path);
            }
            else
            {
                // Still open (being saved or downloaded); look again later.
                pending.TryAdd(path, DateTime.UtcNow);
            }
        }

        if (ready.Count > 0)
        {
            Apply(planner.PlanFiles(ready));
        }
    }

    void Apply(IReadOnlyList<PlannedRename> plans)
    {
        var restored = journal.RestoredPaths();
        plans = plans.Where(p => !restored.Contains(p.SourcePath)).ToList();
        if (plans.Count == 0)
        {
            return;
        }

        var (results, batch) = RenameExecutor.ApplyAndRecord(plans, journal, "watch");
        foreach (var failure in results.Where(r => !r.Succeeded))
        {
            Message?.Invoke($"Couldn't rename {failure.Plan.SourceName}: {failure.Error}");
        }

        if (batch != null)
        {
            BatchApplied?.Invoke(batch);
        }
    }

    void DisposeWatchers()
    {
        foreach (var watcher in watchers)
        {
            watcher.Dispose();
        }

        watchers.Clear();
    }

    public void Dispose()
    {
        timer?.Dispose();
        lock (processingGate)
        {
            DisposeWatchers();
        }
    }
}
