namespace AutoFile.Core;

/// <summary>
/// Where AutoFile keeps its state. Everything lives in an "AutoFile.data" folder next to the
/// executables so the app stays portable (copy the folder, and the config and history go with it).
/// Set the AUTOFILE_DATA environment variable to use a different location.
/// </summary>
public static class AppPaths
{
    public static string DataDirectory { get; } = ResolveDataDirectory();

    public static string ConfigFile => Path.Combine(DataDirectory, "config.json");

    public static string JournalDirectory => Path.Combine(DataDirectory, "journal");

    static string ResolveDataDirectory()
    {
        var overridden = Environment.GetEnvironmentVariable("AUTOFILE_DATA");
        var directory = string.IsNullOrWhiteSpace(overridden)
            ? Path.Combine(AppContext.BaseDirectory, "AutoFile.data")
            : overridden;
        return Path.GetFullPath(directory);
    }
}
