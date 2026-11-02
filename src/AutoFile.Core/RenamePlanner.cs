using System.IO.Enumeration;

namespace AutoFile.Core;

public sealed record PlannedRename(string SourcePath, string TargetPath, DateSource Source)
{
    public string SourceName => Path.GetFileName(SourcePath);

    public string TargetName => Path.GetFileName(TargetPath);
}

/// <summary>
/// Works out which files break the naming convention and what each should be called.
/// Pass <paramref name="convention"/> to use one convention for everything; otherwise each file
/// follows its watched folder's convention, or the default.
/// </summary>
public sealed class RenamePlanner(AutoFileConfig config, IEnumerable<string>? excludedDirectories = null, NamingConvention? convention = null)
{
    readonly string[] excluded = (excludedDirectories ?? [])
        .Select(d => Path.TrimEndingDirectorySeparator(Path.GetFullPath(d)))
        .ToArray();

    readonly IReadOnlyList<NamingConvention> knownConventions = convention == null
        ? config.KnownConventions()
        : [convention, .. config.KnownConventions()];

    public NamingConvention ConventionFor(FileInfo file) => convention ?? config.ConventionFor(file.DirectoryName!);

    public IReadOnlyList<PlannedRename> PlanFolder(string folder, bool recursive)
    {
        var options = new EnumerationOptions
        {
            RecurseSubdirectories = recursive,
            IgnoreInaccessible = true,
            AttributesToSkip = FileAttributes.Hidden | FileAttributes.System | FileAttributes.ReparsePoint,
        };
        return PlanFiles(Directory.EnumerateFiles(folder, "*", options));
    }

    public IReadOnlyList<PlannedRename> PlanFiles(IEnumerable<string> paths)
    {
        var plans = new List<PlannedRename>();
        var takenNamesByDirectory = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);

        foreach (var path in paths.Select(Path.GetFullPath).Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase))
        {
            var file = new FileInfo(path);
            if (!file.Exists || !NeedsRename(file))
            {
                continue;
            }

            var directory = file.DirectoryName!;
            if (!takenNamesByDirectory.TryGetValue(directory, out var taken))
            {
                taken = new HashSet<string>(
                    Directory.EnumerateFileSystemEntries(directory).Select(Path.GetFileName)!,
                    StringComparer.OrdinalIgnoreCase);
                takenNamesByDirectory[directory] = taken;
            }

            var resolved = FileDateResolver.Resolve(file, config.UseMetadataDates, knownConventions);
            var targetName = PickFreeName(ConventionFor(file), resolved, file.Extension, taken);
            taken.Add(targetName);
            plans.Add(new PlannedRename(file.FullName, Path.Combine(directory, targetName), resolved.Source));
        }

        return plans;
    }

    public bool NeedsRename(FileInfo file) =>
        !IsIgnored(file) && !ConventionFor(file).IsConforming(Path.GetFileNameWithoutExtension(file.Name));

    public bool IsIgnored(FileInfo file)
    {
        if ((file.Attributes & (FileAttributes.Hidden | FileAttributes.System)) != 0)
        {
            return true;
        }

        if (config.IgnorePatterns.Any(p => FileSystemName.MatchesSimpleExpression(p, file.Name, ignoreCase: true)))
        {
            return true;
        }

        var directory = file.DirectoryName ?? "";
        if (directory.Split(Path.DirectorySeparatorChar).Any(segment => segment.Equals(".git", StringComparison.OrdinalIgnoreCase)))
        {
            return true;
        }

        return excluded.Any(e => directory.Equals(e, StringComparison.OrdinalIgnoreCase)
                                 || directory.StartsWith(e + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase));
    }

    static string PickFreeName(NamingConvention target, ResolvedName resolved, string extension, HashSet<string> taken)
    {
        var name = target.FileName(resolved.Date, resolved.Title, extension);
        for (var n = 2; taken.Contains(name); n++)
        {
            name = target.FileName(resolved.Date, $"{resolved.Title} ({n})", extension);
        }

        return name;
    }
}
