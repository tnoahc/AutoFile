using System.Text;
using AutoFile.Core;

Console.OutputEncoding = Encoding.UTF8;

var store = new ConfigStore(AppPaths.ConfigFile);
var journal = new RenameJournal(AppPaths.JournalDirectory);

try
{
    return args switch
    {
        ["scan", var folder, .. var rest] => Scan(folder, rest),
        ["undo"] => Undo(null),
        ["undo", var id] => Undo(id),
        ["history", .. var rest] => History(rest),
        ["watch"] => Watch(),
        ["folders"] or ["folders", "list"] => ListFolders(),
        ["folders", "add", var folder, .. var rest] => AddFolder(folder, rest),
        ["folders", "remove", var folder] => RemoveFolder(folder),
        ["convention"] => ShowConventions(),
        ["convention", "set", var pattern] => SetDefaultConvention(pattern),
        ["config"] => ShowConfig(),
        [] or ["help"] or ["--help"] or ["-h"] or ["/?"] => Help(0),
        _ => Help(2),
    };
}
catch (Exception ex) when (ex is InvalidOperationException or IOException or UnauthorizedAccessException or TimeoutException or FormatException)
{
    Console.Error.WriteLine(ex.Message);
    return 1;
}

int Scan(string folder, string[] options)
{
    var recursive = options.Contains("--recursive") || options.Contains("-r");
    var apply = options.Contains("--apply");
    folder = Path.GetFullPath(folder);
    if (!Directory.Exists(folder))
    {
        Console.Error.WriteLine($"Folder not found: {folder}");
        return 1;
    }

    var config = store.Load();
    var conventionOption = OptionValue(options, "--convention");
    var convention = conventionOption != null ? NamingConvention.Parse(conventionOption) : config.ConventionFor(folder);
    var planner = new RenamePlanner(config, [AppPaths.DataDirectory], conventionOption != null ? convention : null);
    var plans = planner.PlanFolder(folder, recursive);
    if (plans.Count == 0)
    {
        Console.WriteLine($"Every file already follows the {convention} convention.");
        return 0;
    }

    Console.WriteLine($"Convention: {convention}   (e.g. {convention.Example(DateOnly.FromDateTime(DateTime.Today))})");
    Console.WriteLine();

    if (!apply)
    {
        PrintPlans(folder, plans);
        Console.WriteLine();
        Console.WriteLine($"{plans.Count} {Plural(plans.Count, "file")} would be renamed. Run again with --apply to rename them.");
        return 0;
    }

    var (results, batch) = RenameExecutor.ApplyAndRecord(plans, journal, "scan");
    var width = Math.Min(results.Max(r => Relative(folder, r.Plan.SourcePath).Length), 60);
    foreach (var result in results)
    {
        var line = $"{Relative(folder, result.Plan.SourcePath).PadRight(width)}  →  {result.Plan.TargetName}";
        Console.WriteLine(result.Succeeded ? $"  {line}" : $"  FAILED {line}: {result.Error}");
    }

    var renamed = results.Count(r => r.Succeeded);
    Console.WriteLine();
    Console.WriteLine($"Renamed {renamed} of {results.Count} {Plural(results.Count, "file")}."
                      + (batch != null ? $" Undo with: autofile undo {batch.Id}" : ""));
    return renamed == results.Count ? 0 : 1;
}

void PrintPlans(string folder, IReadOnlyList<PlannedRename> plans)
{
    var sourceNames = plans.Select(p => Relative(folder, p.SourcePath)).ToList();
    var width = Math.Min(sourceNames.Max(n => n.Length), 60);
    for (var i = 0; i < plans.Count; i++)
    {
        Console.WriteLine($"  {sourceNames[i].PadRight(width)}  →  {plans[i].TargetName}   ({Describe(plans[i].Source)})");
    }
}

int Undo(string? id)
{
    var outcome = journal.Undo(id);
    foreach (var entry in outcome.UndoBatch?.Entries ?? [])
    {
        Console.WriteLine($"  {Path.GetFileName(entry.OldPath)}  →  {Path.GetFileName(entry.NewPath)}");
    }

    foreach (var problem in outcome.Problems)
    {
        Console.WriteLine($"  SKIPPED {problem}");
    }

    var restored = outcome.UndoBatch?.Entries.Count ?? 0;
    Console.WriteLine();
    Console.WriteLine($"Restored {restored} of {outcome.Original.Entries.Count} {Plural(outcome.Original.Entries.Count, "file")} from batch {outcome.Original.Id}.");
    return outcome.Problems.Count == 0 ? 0 : 1;
}

int History(string[] options)
{
    var limit = 20;
    var limitIndex = Array.FindIndex(options, o => o is "--limit" or "-n");
    if (limitIndex >= 0 && (limitIndex + 1 >= options.Length || !int.TryParse(options[limitIndex + 1], out limit)))
    {
        return Help(2);
    }

    var commits = journal.History(limit);
    if (commits.Count == 0)
    {
        Console.WriteLine("No renames recorded yet.");
        return 0;
    }

    foreach (var commit in commits)
    {
        Console.WriteLine($"{commit.Sha[..8]}  {commit.When.LocalDateTime:yyyy-MM-dd HH:mm}  {commit.Summary}   [{commit.BatchId}]");
    }

    Console.WriteLine();
    Console.WriteLine($"Full history is a git repo at {journal.Location}");
    return 0;
}

int Watch()
{
    var config = store.Load();
    if (config.WatchedFolders.Count == 0)
    {
        Console.Error.WriteLine("No folders are being watched. Add one with: autofile folders add <folder>");
        return 1;
    }

    using var watcher = new FolderWatcher(config, journal, [AppPaths.DataDirectory]);
    watcher.Message += message => Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] {message}");
    watcher.BatchApplied += batch =>
    {
        foreach (var entry in batch.Entries)
        {
            Console.WriteLine($"[{batch.Timestamp:HH:mm:ss}] {entry.OldPath}  →  {Path.GetFileName(entry.NewPath)}");
        }
    };

    foreach (var folder in config.WatchedFolders)
    {
        Console.WriteLine($"Watching {folder.Path}{(folder.Recursive ? " (and subfolders)" : "")}");
    }

    Console.WriteLine("Press Ctrl+C to stop.");
    using var stop = new ManualResetEventSlim();
    Console.CancelKeyPress += (_, e) =>
    {
        e.Cancel = true;
        stop.Set();
    };
    watcher.Start();
    stop.Wait();
    return 0;
}

int ListFolders()
{
    var config = store.Load();
    if (config.WatchedFolders.Count == 0)
    {
        Console.WriteLine("No folders are being watched. Add one with: autofile folders add <folder>");
        return 0;
    }

    foreach (var folder in config.WatchedFolders)
    {
        Console.WriteLine($"  {folder.Path}{(folder.Recursive ? "  (including subfolders)" : "")}"
                          + $"  [{folder.Convention ?? $"default: {config.Convention}"}]");
    }

    return 0;
}

int ShowConventions()
{
    var config = store.Load();
    var today = DateOnly.FromDateTime(DateTime.Today);
    Console.WriteLine($"Default convention: {config.Convention}   (e.g. {NamingConvention.Parse(config.Convention).Example(today)})");
    Console.WriteLine();
    Console.WriteLine("Presets:");
    foreach (var preset in NamingConvention.Presets)
    {
        Console.WriteLine($"  {preset,-22} e.g. {NamingConvention.Parse(preset).Example(today)}");
    }

    Console.WriteLine();
    Console.WriteLine("Write your own with YYYY or YY, MM, DD and the word Title, e.g. \"Title - DD.MM.YYYY\".");
    Console.WriteLine("Change the default with: autofile convention set \"YYYY-MM-DD Title\"");
    return 0;
}

int SetDefaultConvention(string pattern)
{
    var convention = NamingConvention.Parse(pattern);
    var config = store.Load();
    config.Convention = convention.Pattern;
    store.Save(config);
    Console.WriteLine($"Default convention is now {convention}   (e.g. {convention.Example(DateOnly.FromDateTime(DateTime.Today))})");

    var affected = config.WatchedFolders.Count(f => f.Convention == null);
    if (affected > 0)
    {
        Console.WriteLine($"Files in {affected} watched {(affected == 1 ? "folder" : "folders")} will be renamed to match when the watcher next runs.");
    }

    return 0;
}

int AddFolder(string folder, string[] options)
{
    folder = Path.TrimEndingDirectorySeparator(Path.GetFullPath(folder));
    if (!Directory.Exists(folder))
    {
        Console.Error.WriteLine($"Folder not found: {folder}");
        return 1;
    }

    var recursive = options.Contains("--recursive") || options.Contains("-r");
    var convention = OptionValue(options, "--convention") is { } pattern ? NamingConvention.Parse(pattern).Pattern : null;
    var config = store.Load();
    config.WatchedFolders.RemoveAll(f => f.Path.Equals(folder, StringComparison.OrdinalIgnoreCase));
    config.WatchedFolders.Add(new WatchedFolder { Path = folder, Recursive = recursive, Convention = convention });
    store.Save(config);
    Console.WriteLine($"Now watching {folder}{(recursive ? " (including subfolders)" : "")} using {convention ?? config.Convention}.");
    return 0;
}

int RemoveFolder(string folder)
{
    folder = Path.TrimEndingDirectorySeparator(Path.GetFullPath(folder));
    var config = store.Load();
    if (config.WatchedFolders.RemoveAll(f => f.Path.Equals(folder, StringComparison.OrdinalIgnoreCase)) == 0)
    {
        Console.Error.WriteLine($"{folder} isn't being watched.");
        return 1;
    }

    store.Save(config);
    Console.WriteLine($"Stopped watching {folder}.");
    return 0;
}

int ShowConfig()
{
    store.Load();
    Console.WriteLine($"Config:  {store.Path}");
    Console.WriteLine($"Journal: {journal.Location}");
    return 0;
}

static int Help(int exitCode)
{
    var output = exitCode == 0 ? Console.Out : Console.Error;
    output.WriteLine("""
        AutoFile renames files to a date-based naming convention ("YYMMDD Title" by default).

        Usage:
          autofile scan <folder> [--recursive] [--convention <c>] [--apply]
                                                Preview renames (or apply them with --apply)
          autofile undo [<batch-id>]            Undo the last batch of renames, or a specific one
          autofile history [--limit <n>]        Show recent rename batches
          autofile watch                        Watch the configured folders in this window
          autofile folders [list]               List watched folders
          autofile folders add <folder> [--recursive] [--convention <c>]
                                                Watch a folder (used by 'watch' and the app)
          autofile folders remove <folder>      Stop watching a folder
          autofile convention                   Show the default convention and the presets
          autofile convention set <c>           Change the default convention
          autofile config                       Show where the config and journal live

        A convention uses YYYY or YY, MM, DD and the word Title, e.g. "YYYY-MM-DD Title" or
        "Title - DD.MM.YYYY". Quote it when it contains spaces.

        Dates come from a date already in the file name, then photo/document metadata,
        then the earlier of the file's created and modified times.
        """);
    return exitCode;
}

static string Describe(DateSource source) => source switch
{
    DateSource.FileName => "date from name",
    DateSource.Metadata => "date from metadata",
    _ => "date from file times",
};

static string Relative(string root, string path) => Path.GetRelativePath(root, path);

static string? OptionValue(string[] options, string name)
{
    var index = Array.IndexOf(options, name);
    return index >= 0 && index + 1 < options.Length ? options[index + 1] : null;
}

static string Plural(int count, string word) => count == 1 ? word : word + "s";
