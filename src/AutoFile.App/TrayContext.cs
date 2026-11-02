namespace AutoFile.App;

/// <summary>
/// Owns the app's lifetime: the tray icon, the main window, and the background watcher.
/// Closing the window leaves AutoFile watching in the tray (if any folders are watched);
/// "Exit" in the tray menu quits.
/// </summary>
sealed class TrayContext : ApplicationContext
{
    readonly AppState state = new();
    readonly NotifyIcon trayIcon;
    readonly RegisteredWaitHandle showSignalRegistration;
    readonly SynchronizationContext ui;
    MainWindow? window;
    bool exiting;
    bool toldAboutTray;

    public TrayContext(EventWaitHandle showSignal, bool startMinimized, string? folder)
    {
        ui = SynchronizationContext.Current!;

        trayIcon = new NotifyIcon
        {
            Icon = AppIcon.Small,
            Text = "AutoFile",
            Visible = true,
            ContextMenuStrip = new ContextMenuStrip(),
        };
        trayIcon.ContextMenuStrip.Opening += (_, _) => BuildMenu(trayIcon.ContextMenuStrip);
        trayIcon.DoubleClick += (_, _) => ShowWindow();
        trayIcon.BalloonTipClicked += (_, _) => ShowWindow(MainWindow.Page.History);

        state.BatchApplied += batch =>
        {
            var count = batch.Entries.Count;
            var names = string.Join("\n", batch.Entries.Take(3).Select(e => Path.GetFileName(e.NewPath)));
            ShowBalloon($"Renamed {count} {(count == 1 ? "file" : "files")}", names + (count > 3 ? $"\n…and {count - 3} more" : ""));
        };
        state.WatcherMessage += message => ShowBalloon("AutoFile", message, ToolTipIcon.Warning);

        // A second launch of the exe signals this one to show its window.
        showSignalRegistration = ThreadPool.RegisterWaitForSingleObject(
            showSignal, (_, _) => ui.Post(_ => ShowWindow(), null), null, Timeout.Infinite, executeOnlyOnce: false);

        if (!startMinimized)
        {
            ShowWindow();
            if (folder != null)
            {
                window!.PreviewFolder(folder, recursive: false);
            }
        }
    }

    void ShowWindow(MainWindow.Page? page = null)
    {
        if (window == null || window.IsDisposed)
        {
            window = new MainWindow(state);
            window.FormClosing += OnWindowClosing;
        }

        if (page != null)
        {
            window.ShowPage(page.Value);
        }

        window.Show();
        if (window.WindowState == FormWindowState.Minimized)
        {
            window.WindowState = FormWindowState.Normal;
        }

        window.Activate();
    }

    void OnWindowClosing(object? sender, FormClosingEventArgs e)
    {
        if (exiting || e.CloseReason != CloseReason.UserClosing)
        {
            return;
        }

        if (state.Config.WatchedFolders.Count == 0)
        {
            // Nothing to watch, so there's no reason to linger in the tray.
            ExitThread();
            return;
        }

        e.Cancel = true;
        window!.Hide();
        if (!toldAboutTray)
        {
            toldAboutTray = true;
            ShowBalloon("AutoFile is still watching", "It keeps your folders tidy from the tray. Right-click the icon to exit.");
        }
    }

    void BuildMenu(ContextMenuStrip menu)
    {
        menu.Items.Clear();

        var count = state.Config.WatchedFolders.Count;
        var status = state.Paused ? "Watching paused" : $"Watching {count} {(count == 1 ? "folder" : "folders")}";
        menu.Items.Add(new ToolStripMenuItem(status) { Enabled = false });
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(new ToolStripMenuItem("Open AutoFile", null, (_, _) => ShowWindow()) { Font = new Font(menu.Font, FontStyle.Bold) });
        menu.Items.Add(new ToolStripMenuItem("Pause watching", null, (_, _) => state.Paused = !state.Paused) { Checked = state.Paused });
        menu.Items.Add("Undo last batch", null, (_, _) => UndoLast());
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Exit", null, (_, _) => ExitThread());
    }

    void UndoLast()
    {
        try
        {
            var outcome = state.Journal.Undo();
            state.NotifyJournalChanged();
            var restored = outcome.UndoBatch?.Entries.Count ?? 0;
            ShowBalloon("Undo", $"Restored {restored} of {outcome.Original.Entries.Count} original names."
                                + (outcome.Problems.Count > 0 ? $" {outcome.Problems.Count} couldn't be restored." : ""));
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or TimeoutException)
        {
            ShowBalloon("Undo", ex.Message);
        }
    }

    void ShowBalloon(string title, string text, ToolTipIcon kind = ToolTipIcon.Info)
    {
        trayIcon.BalloonTipTitle = title;
        trayIcon.BalloonTipText = string.IsNullOrEmpty(text) ? " " : text;
        trayIcon.BalloonTipIcon = kind;
        trayIcon.ShowBalloonTip(5000);
    }

    protected override void ExitThreadCore()
    {
        exiting = true;
        showSignalRegistration.Unregister(null);
        window?.Close();
        state.Dispose();
        trayIcon.Visible = false;
        trayIcon.Dispose();
        base.ExitThreadCore();
    }
}
