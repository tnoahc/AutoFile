namespace AutoFile.Core;

public sealed record RenameResult(PlannedRename Plan, string? Error)
{
    public bool Succeeded => Error == null;
}

public static class RenameExecutor
{
    public static IReadOnlyList<RenameResult> Apply(IEnumerable<PlannedRename> plans)
    {
        var results = new List<RenameResult>();
        foreach (var plan in plans)
        {
            try
            {
                // Never overwrite: if something took the name since planning, report it instead.
                File.Move(plan.SourcePath, plan.TargetPath, overwrite: false);
                results.Add(new RenameResult(plan, null));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                results.Add(new RenameResult(plan, ex.Message));
            }
        }

        return results;
    }

    /// <summary>Applies the plans and records the successful ones in the journal as one batch.</summary>
    public static (IReadOnlyList<RenameResult> Results, RenameBatch? Batch) ApplyAndRecord(
        IEnumerable<PlannedRename> plans, RenameJournal journal, string origin)
    {
        var results = Apply(plans);
        var entries = results
            .Where(r => r.Succeeded)
            .Select(r => new RenameEntry(r.Plan.SourcePath, r.Plan.TargetPath, r.Plan.Source.ToString()))
            .ToList();
        var batch = entries.Count > 0 ? journal.Record(origin, entries) : null;
        return (results, batch);
    }

    /// <summary>True when nothing else has the file open, i.e. it has finished downloading or saving.</summary>
    public static bool IsReady(string path)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }
}
