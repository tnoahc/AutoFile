using AutoFile.Core;
using Microsoft.Win32;

namespace AutoFile.App;

/// <summary>
/// What the window and the tray icon share: settings, the journal and the background watcher.
/// All events are raised on the UI thread.
/// </summary>
sealed class AppState : IDisposable
{
    const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    const string RunValueName = "AutoFile";

    readonly SynchronizationContext ui;
    readonly ConfigStore store = new(AppPaths.ConfigFile);
    readonly FileSystemWatcher configWatcher;
    readonly System.Windows.Forms.Timer configReloadTimer = new() { Interval = 500 };

    public AppState()
    {
        ui = SynchronizationContext.Current ?? new WindowsFormsSynchronizationContext();
        Config = store.Load();

        Watcher = new FolderWatcher(Config, Journal, [AppPaths.DataDirectory]);
        Watcher.BatchApplied += batch => ui.Post(_ =>
        {
            BatchApplied?.Invoke(batch);
            JournalChanged?.Invoke();
        }, null);
        Watcher.Message += message => ui.Post(_ => WatcherMessage?.Invoke(message), null);

        // Pick up folder changes made with the command-line tool while the app is running.
        configWatcher = new FileSystemWatcher(AppPaths.DataDirectory, Path.GetFileName(AppPaths.ConfigFile))
        {
            NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName,
            EnableRaisingEvents = true,
        };
        configWatcher.Changed += (_, _) => ui.Post(_ => RestartReloadTimer(), null);
        configWatcher.Renamed += (_, _) => ui.Post(_ => RestartReloadTimer(), null);
        configReloadTimer.Tick += (_, _) =>
        {
            configReloadTimer.Stop();
            ReloadConfig();
        };

        Watcher.Start();
    }

    public RenameJournal Journal { get; } = new(AppPaths.JournalDirectory);

    public FolderWatcher Watcher { get; }

    public AutoFileConfig Config { get; private set; }

    /// <summary>Watched folders, pause state or startup setting changed.</summary>
    public event Action? SettingsChanged;

    /// <summary>The watcher renamed some files.</summary>
    public event Action<RenameBatch>? BatchApplied;

    /// <summary>Anything was added to the journal (renames from any source, or an undo).</summary>
    public event Action? JournalChanged;

    public event Action<string>? WatcherMessage;

    public bool Paused
    {
        get => Watcher.Paused;
        set
        {
            Watcher.Paused = value;
            SettingsChanged?.Invoke();
        }
    }

    public bool StartWithWindows
    {
        get
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath);
            return key?.GetValue(RunValueName) is string value
                   && value.Contains(Environment.ProcessPath!, StringComparison.OrdinalIgnoreCase);
        }
        set
        {
            using var key = Registry.CurrentUser.CreateSubKey(RunKeyPath);
            if (value)
            {
                key.SetValue(RunValueName, $"\"{Environment.ProcessPath}\" --minimized");
            }
            else
            {
                key.DeleteValue(RunValueName, throwOnMissingValue: false);
            }

            SettingsChanged?.Invoke();
        }
    }

    public void UpdateConfig(Action<AutoFileConfig> change)
    {
        var fresh = store.Load();
        change(fresh);
        store.Save(fresh);
        Apply(fresh);
    }

    public void NotifyJournalChanged() => JournalChanged?.Invoke();

    void RestartReloadTimer()
    {
        configReloadTimer.Stop();
        configReloadTimer.Start();
    }

    void ReloadConfig()
    {
        try
        {
            Apply(store.Load());
        }
        catch (Exception ex) when (ex is IOException or System.Text.Json.JsonException)
        {
            WatcherMessage?.Invoke($"Couldn't reload settings: {ex.Message}");
        }
    }

    void Apply(AutoFileConfig config)
    {
        Config = config;
        Watcher.Reconfigure(config);
        SettingsChanged?.Invoke();
    }

    public void Dispose()
    {
        Watcher.Dispose();
        configWatcher.Dispose();
        configReloadTimer.Dispose();
    }
}
