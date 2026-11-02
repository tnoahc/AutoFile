using System.Text.Json;

namespace AutoFile.Core;

public sealed class WatchedFolder
{
    public string Path { get; set; } = "";

    public bool Recursive { get; set; }

    /// <summary>This folder's own naming convention, or null to use the default.</summary>
    public string? Convention { get; set; }
}

public sealed class AutoFileConfig
{
    /// <summary>The naming convention used everywhere a watched folder doesn't set its own.</summary>
    public string Convention { get; set; } = NamingConvention.DefaultPattern;

    /// <summary>Folders the background watcher keeps tidy.</summary>
    public List<WatchedFolder> WatchedFolders { get; set; } = [];

    /// <summary>Wildcard patterns (matched against the file name) that are never renamed.</summary>
    public List<string> IgnorePatterns { get; set; } =
    [
        "desktop.ini",
        "Thumbs.db",
        ".*",
        "~$*",
        "*.tmp",
        "*.temp",
        "*.part",
        "*.partial",
        "*.crdownload",
        "*.download",
        "*.lnk",
        "*.url",
    ];

    /// <summary>Read dates from photo EXIF, Office document properties and PDF info before falling back to file timestamps.</summary>
    public bool UseMetadataDates { get; set; } = true;

    /// <summary>How long a new or changed file must sit untouched before the watcher renames it.</summary>
    public int StableSeconds { get; set; } = 5;

    /// <summary>How often the watcher does a full sweep of every folder, to catch anything it missed.</summary>
    public int RescanMinutes { get; set; } = 15;

    /// <summary>The watched folder that contains <paramref name="directory"/> (the innermost one if they nest).</summary>
    public WatchedFolder? WatchedFolderFor(string directory)
    {
        var full = System.IO.Path.TrimEndingDirectorySeparator(System.IO.Path.GetFullPath(directory));
        return WatchedFolders
            .Where(f => full.Equals(f.Path, StringComparison.OrdinalIgnoreCase)
                        || full.StartsWith(f.Path + System.IO.Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            .MaxBy(f => f.Path.Length);
    }

    /// <summary>The convention for files in <paramref name="directory"/>: its watched folder's own, or the default.</summary>
    public NamingConvention ConventionFor(string directory) =>
        NamingConvention.Parse(WatchedFolderFor(directory)?.Convention ?? Convention);

    /// <summary>
    /// Every convention a file might already be named in: the ones configured here, then the presets.
    /// Names in any of these are converted cleanly rather than treated as having no date.
    /// </summary>
    public IReadOnlyList<NamingConvention> KnownConventions() =>
        new[] { Convention }
            .Concat(WatchedFolders.Select(f => f.Convention).OfType<string>())
            .Concat(NamingConvention.Presets)
            .Select(p => NamingConvention.TryParse(p, out var convention, out _) ? convention : null)
            .OfType<NamingConvention>()
            .DistinctBy(c => c.Pattern, StringComparer.OrdinalIgnoreCase)
            .ToList();
}

public sealed class ConfigStore(string path)
{
    static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public string Path { get; } = path;

    /// <summary>Loads the config, creating a default one on first run.</summary>
    public AutoFileConfig Load()
    {
        if (!File.Exists(Path))
        {
            var fresh = new AutoFileConfig();
            Save(fresh);
            return fresh;
        }

        using var stream = File.OpenRead(Path);
        return JsonSerializer.Deserialize<AutoFileConfig>(stream, JsonOptions) ?? new AutoFileConfig();
    }

    public void Save(AutoFileConfig config)
    {
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path)!);
        var temp = Path + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(config, JsonOptions));
        File.Move(temp, Path, overwrite: true);
    }
}
