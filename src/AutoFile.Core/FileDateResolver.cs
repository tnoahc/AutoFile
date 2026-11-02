namespace AutoFile.Core;

public enum DateSource
{
    /// <summary>A date already written in the file name.</summary>
    FileName,

    /// <summary>EXIF "date taken", Office "created" property, or PDF creation date.</summary>
    Metadata,

    /// <summary>The earlier of the file's created and modified timestamps.</summary>
    FileSystem,
}

public sealed record ResolvedName(DateOnly Date, DateSource Source, string Title);

/// <summary>Picks the best available date for a file and the title that goes with it.</summary>
public static class FileDateResolver
{
    public static ResolvedName Resolve(FileInfo file, bool useMetadata, IEnumerable<NamingConvention> knownConventions)
    {
        var stem = Path.GetFileNameWithoutExtension(file.Name);
        var today = DateOnly.FromDateTime(DateTime.Now);

        // Already named in a recognised convention (e.g. switching from YYMMDD to YYYY-MM-DD): convert it.
        foreach (var convention in knownConventions)
        {
            if (convention.TryMatch(stem, today, out var date, out var title))
            {
                return new ResolvedName(date, DateSource.FileName, title);
            }
        }

        var fromName = FilenameDateParser.Extract(stem, today);
        if (fromName != null)
        {
            return new ResolvedName(fromName.Date, DateSource.FileName, fromName.Title);
        }

        if (useMetadata && MetadataDateReader.TryRead(file.FullName) is { } metadataDate)
        {
            var date = DateOnly.FromDateTime(metadataDate);
            if (date.Year >= 1970 && date <= today.AddDays(1))
            {
                return new ResolvedName(date, DateSource.Metadata, stem.Trim());
            }
        }

        // Windows resets "created" when a file is copied or downloaded, so a modified date
        // earlier than the created date is the better clue to when the document began.
        var earliest = file.CreationTime < file.LastWriteTime ? file.CreationTime : file.LastWriteTime;
        return new ResolvedName(DateOnly.FromDateTime(earliest), DateSource.FileSystem, stem.Trim());
    }
}
