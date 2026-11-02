using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using LibGit2Sharp;

namespace AutoFile.Core;

public sealed record RenameEntry(string OldPath, string NewPath, string? DateSource = null);

public sealed record RenameBatch(string Id, DateTimeOffset Timestamp, string Origin, string? UndoOf, List<RenameEntry> Entries);

public sealed record JournalCommit(string Sha, DateTimeOffset When, string Summary, string? BatchId);

public sealed record UndoOutcome(RenameBatch Original, RenameBatch? UndoBatch, IReadOnlyList<string> Problems);

/// <summary>
/// A git repository that records every batch of renames as a commit, so there is a full,
/// tamper-evident history and any batch can be reversed. Each batch is stored as
/// batches/&lt;id&gt;.json; the repo can also be browsed with any git tool.
/// </summary>
public sealed class RenameJournal
{
    const string BatchFolder = "batches";
    const string BatchTrailer = "Batch: ";
    static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    readonly string mutexName;
    HashSet<string>? restoredPaths;
    DateTime restoredPathsStamp;

    public RenameJournal(string location)
    {
        Location = Path.GetFullPath(location);
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Location.ToUpperInvariant())))[..16];
        mutexName = $@"Local\AutoFile-journal-{hash}";
    }

    public string Location { get; }

    public RenameBatch Record(string origin, IReadOnlyList<RenameEntry> entries) =>
        WithLock(() => RecordCore(origin, entries, undoOf: null));

    public IReadOnlyList<JournalCommit> History(int max)
    {
        if (!Repository.IsValid(Location))
        {
            return [];
        }

        using var repo = new Repository(Location);
        if (repo.Head.Tip == null)
        {
            return [];
        }

        return repo.Commits
            .Take(max)
            .Select(c => new JournalCommit(c.Sha, c.Author.When, c.MessageShort, ParseBatchId(c.Message)))
            .ToList();
    }

    /// <summary>Finds a batch by (a prefix of) its id or of its commit SHA.</summary>
    public RenameBatch? Find(string idOrSha)
    {
        var batches = Batches();
        var byId = batches.Where(b => b.Id.StartsWith(idOrSha, StringComparison.OrdinalIgnoreCase)).ToList();
        if (byId.Count == 1)
        {
            return byId[0];
        }

        var commit = History(int.MaxValue).FirstOrDefault(c => c.Sha.StartsWith(idOrSha, StringComparison.OrdinalIgnoreCase));
        return commit?.BatchId == null ? null : batches.FirstOrDefault(b => b.Id == commit.BatchId);
    }

    /// <summary>
    /// Reverses a batch (the most recent one not yet undone when <paramref name="idOrSha"/> is null)
    /// and records the reversal as its own commit.
    /// </summary>
    public UndoOutcome Undo(string? idOrSha = null) => WithLock(() =>
    {
        var batches = Batches();
        var undone = batches.Where(b => b.UndoOf != null).Select(b => b.UndoOf!).ToHashSet();

        var target = idOrSha == null
            ? batches.Where(b => b.UndoOf == null && !undone.Contains(b.Id)).MaxBy(b => b.Timestamp)
            : Find(idOrSha);

        if (target == null)
        {
            throw new InvalidOperationException(idOrSha == null ? "There is nothing to undo." : $"No batch matches '{idOrSha}'.");
        }

        if (target.UndoOf != null)
        {
            throw new InvalidOperationException($"Batch {target.Id} is itself an undo and can't be reversed.");
        }

        if (undone.Contains(target.Id))
        {
            throw new InvalidOperationException($"Batch {target.Id} has already been undone.");
        }

        var restored = new List<RenameEntry>();
        var problems = new List<string>();
        foreach (var entry in Enumerable.Reverse(target.Entries))
        {
            if (!File.Exists(entry.NewPath))
            {
                problems.Add($"{entry.NewPath} no longer exists.");
            }
            else if (File.Exists(entry.OldPath))
            {
                problems.Add($"Can't restore {Path.GetFileName(entry.OldPath)}: another file now has that name.");
            }
            else
            {
                try
                {
                    File.Move(entry.NewPath, entry.OldPath);
                    restored.Add(new RenameEntry(entry.NewPath, entry.OldPath));
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    problems.Add($"Can't restore {Path.GetFileName(entry.OldPath)}: {ex.Message}");
                }
            }
        }

        var undoBatch = restored.Count > 0 ? RecordCore("undo", restored, target.Id) : null;
        return new UndoOutcome(target, undoBatch, problems);
    });

    /// <summary>
    /// Paths that an undo put back to their original names. The watcher leaves these alone,
    /// otherwise it would immediately rename them again.
    /// </summary>
    public IReadOnlySet<string> RestoredPaths()
    {
        var folder = Path.Combine(Location, BatchFolder);
        var stamp = Directory.Exists(folder) ? Directory.GetLastWriteTimeUtc(folder) : DateTime.MinValue;
        if (restoredPaths == null || stamp != restoredPathsStamp)
        {
            restoredPaths = Batches()
                .Where(b => b.UndoOf != null)
                .SelectMany(b => b.Entries)
                .Select(e => e.NewPath)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            restoredPathsStamp = stamp;
        }

        return restoredPaths;
    }

    RenameBatch RecordCore(string origin, IReadOnlyList<RenameEntry> entries, string? undoOf)
    {
        var now = DateTimeOffset.Now;
        var batch = new RenameBatch($"{now:yyyyMMdd-HHmmss}-{Guid.NewGuid().ToString("N")[..6]}", now, origin, undoOf, [.. entries]);

        Directory.CreateDirectory(Path.Combine(Location, BatchFolder));
        if (!Repository.IsValid(Location))
        {
            Repository.Init(Location);
        }

        var relativePath = $"{BatchFolder}/{batch.Id}.json";
        File.WriteAllText(Path.Combine(Location, BatchFolder, batch.Id + ".json"), JsonSerializer.Serialize(batch, JsonOptions));

        using var repo = new Repository(Location);
        Commands.Stage(repo, relativePath);
        var signature = new Signature("AutoFile", "autofile@localhost", now);
        repo.Commit(BuildMessage(batch), signature, signature);
        return batch;
    }

    static string BuildMessage(RenameBatch batch)
    {
        var count = batch.Entries.Count;
        var files = count == 1 ? "file" : "files";
        var subject = batch.UndoOf == null
            ? $"{batch.Origin}: renamed {count} {files}"
            : $"undo: restored {count} {files} from {batch.UndoOf}";

        var body = new StringBuilder();
        body.AppendLine(subject).AppendLine().AppendLine(BatchTrailer + batch.Id).AppendLine();
        foreach (var entry in batch.Entries)
        {
            body.AppendLine($"{entry.OldPath} -> {Path.GetFileName(entry.NewPath)}");
        }

        return body.ToString();
    }

    static string? ParseBatchId(string message) =>
        message.Split('\n')
            .Select(line => line.Trim())
            .FirstOrDefault(line => line.StartsWith(BatchTrailer, StringComparison.Ordinal))?[BatchTrailer.Length..];

    /// <summary>Every recorded batch, newest first.</summary>
    public IReadOnlyList<RenameBatch> Batches()
    {
        var folder = Path.Combine(Location, BatchFolder);
        if (!Directory.Exists(folder))
        {
            return [];
        }

        return Directory.EnumerateFiles(folder, "*.json")
            .Select(path => JsonSerializer.Deserialize<RenameBatch>(File.ReadAllText(path), JsonOptions))
            .OfType<RenameBatch>()
            .OrderByDescending(b => b.Timestamp)
            .ToList();
    }

    /// <summary>The CLI and tray app can run at the same time, so journal writes are serialised across processes.</summary>
    T WithLock<T>(Func<T> action)
    {
        using var mutex = new Mutex(false, mutexName);
        bool owned;
        try
        {
            owned = mutex.WaitOne(TimeSpan.FromSeconds(30));
        }
        catch (AbandonedMutexException)
        {
            owned = true;
        }

        if (!owned)
        {
            throw new TimeoutException("Timed out waiting for another AutoFile process to finish writing the journal.");
        }

        try
        {
            return action();
        }
        finally
        {
            mutex.ReleaseMutex();
        }
    }
}
