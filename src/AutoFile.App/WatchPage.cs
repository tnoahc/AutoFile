using System.Diagnostics;
using AutoFile.Core;

namespace AutoFile.App;

/// <summary>Folders the background watcher keeps tidy, plus pause and start-with-Windows switches.</summary>
sealed class WatchPage : UserControl
{
    readonly AppState state;
    readonly Action<string, bool> previewFolder;
    readonly ListView list = new()
    {
        Dock = DockStyle.Fill,
        View = View.Details,
        FullRowSelect = true,
        HideSelection = false,
        MultiSelect = false,
    };
    readonly Button addButton = new() { Text = "Add folder…", AutoSize = true };
    readonly Button removeButton = new() { Text = "Stop watching", AutoSize = true };
    readonly Button openButton = new() { Text = "Open", AutoSize = true };
    readonly Button conventionButton = new() { Text = "Convention…", AutoSize = true };
    readonly CheckBox subfoldersBox = new() { Text = "Include subfolders", AutoSize = true };
    readonly Label defaultConventionLabel = new() { AutoSize = true, Anchor = AnchorStyles.Left };
    readonly Button changeDefaultButton = new() { Text = "Change…", AutoSize = true };
    readonly CheckBox pauseBox = new() { Text = "Pause watching", AutoSize = true };
    readonly CheckBox startupBox = new() { Text = "Start AutoFile in the tray when I sign in to Windows", AutoSize = true };
    readonly Label activityLabel = new() { AutoSize = true, ForeColor = SystemColors.GrayText };
    bool refreshing;

    public WatchPage(AppState state, Action<string, bool> previewFolder)
    {
        this.state = state;
        this.previewFolder = previewFolder;

        list.Columns.Add("Folder", 380);
        list.Columns.Add("Subfolders", 90);
        list.Columns.Add("Convention", 200);
        foreach (ColumnHeader column in list.Columns)
        {
            column.Width = LogicalToDeviceUnits(column.Width);
        }

        var intro = new Label
        {
            Text = "AutoFile renames files in these folders automatically, a few seconds after they're added or saved. "
                   + "It keeps working from the tray while this window is closed.",
            AutoSize = true,
            Dock = DockStyle.Fill,
            Margin = new Padding(0, 0, 0, LogicalToDeviceUnits(8)),
        };

        var buttons = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.TopDown, WrapContents = false, Dock = DockStyle.Fill };
        buttons.Controls.AddRange([addButton, removeButton, openButton, conventionButton, subfoldersBox]);
        foreach (Control button in buttons.Controls)
        {
            button.MinimumSize = new Size(LogicalToDeviceUnits(130), 0);
        }

        var options = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.TopDown, WrapContents = false, Dock = DockStyle.Fill };
        var defaultRow = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = Padding.Empty };
        defaultRow.Controls.AddRange([defaultConventionLabel, changeDefaultButton]);
        options.Controls.AddRange([defaultRow, pauseBox, startupBox, activityLabel]);

        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 3 };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.Controls.Add(intro, 0, 0);
        layout.SetColumnSpan(intro, 2);
        layout.Controls.Add(list, 0, 1);
        layout.Controls.Add(buttons, 1, 1);
        layout.Controls.Add(options, 0, 2);
        layout.SetColumnSpan(options, 2);
        Controls.Add(layout);

        addButton.Click += (_, _) => AddFolder();
        removeButton.Click += (_, _) => RemoveSelected();
        openButton.Click += (_, _) => OpenSelected();
        conventionButton.Click += (_, _) => ChangeFolderConvention();
        changeDefaultButton.Click += (_, _) => ChangeDefaultConvention();
        list.DoubleClick += (_, _) => OpenSelected();
        list.SelectedIndexChanged += (_, _) => UpdateButtons();
        subfoldersBox.CheckedChanged += (_, _) => SetSubfolders(subfoldersBox.Checked);
        pauseBox.CheckedChanged += (_, _) =>
        {
            if (!refreshing)
            {
                state.Paused = pauseBox.Checked;
            }
        };
        startupBox.CheckedChanged += (_, _) =>
        {
            if (!refreshing)
            {
                state.StartWithWindows = startupBox.Checked;
            }
        };

        state.SettingsChanged += RefreshFromState;
        state.BatchApplied += batch => activityLabel.Text =
            $"Last renamed {batch.Entries.Count} {(batch.Entries.Count == 1 ? "file" : "files")} at {batch.Timestamp.LocalDateTime:HH:mm}.";
        state.WatcherMessage += message => activityLabel.Text = message;
        Disposed += (_, _) => state.SettingsChanged -= RefreshFromState;

        RefreshFromState();
    }

    WatchedFolder? Selected =>
        list.SelectedItems.Count == 1 ? (WatchedFolder)list.SelectedItems[0].Tag! : null;

    void RefreshFromState()
    {
        refreshing = true;
        var selectedPath = Selected?.Path;
        list.BeginUpdate();
        list.Items.Clear();
        foreach (var folder in state.Config.WatchedFolders)
        {
            var item = new ListViewItem(folder.Path) { Tag = folder };
            item.SubItems.Add(folder.Recursive ? "Included" : "—");
            item.SubItems.Add(folder.Convention ?? $"Default ({state.Config.Convention})");
            list.Items.Add(item);
            item.Selected = folder.Path.Equals(selectedPath, StringComparison.OrdinalIgnoreCase);
        }

        list.EndUpdate();
        defaultConventionLabel.Text = $"Default naming convention:  {state.Config.Convention}";
        pauseBox.Checked = state.Paused;
        startupBox.Checked = state.StartWithWindows;
        var count = state.Config.WatchedFolders.Count;
        activityLabel.Text = count == 0
            ? "No folders yet. Click \"Add folder…\" to choose one."
            : state.Paused
                ? "Watching is paused."
                : $"Watching {count} {(count == 1 ? "folder" : "folders")}.";

        refreshing = false;
        UpdateButtons();
    }

    void UpdateButtons()
    {
        var selected = Selected;
        removeButton.Enabled = openButton.Enabled = conventionButton.Enabled = subfoldersBox.Enabled = selected != null;
        refreshing = true;
        subfoldersBox.Checked = selected?.Recursive ?? false;
        refreshing = false;
    }

    void AddFolder()
    {
        using var dialog = new FolderBrowserDialog
        {
            Description = "Choose a folder for AutoFile to keep tidy",
            UseDescriptionForTitle = true,
        };
        if (dialog.ShowDialog(this) != DialogResult.OK)
        {
            return;
        }

        var path = Path.TrimEndingDirectorySeparator(dialog.SelectedPath);
        var watch = new TaskDialogButton("Watch folder");
        var preview = new TaskDialogButton("Preview first");
        var subfolders = new TaskDialogVerificationCheckBox("Include subfolders");
        var page = new TaskDialogPage
        {
            Caption = "AutoFile",
            Heading = $"Watch {Path.GetFileName(path)}?",
            Text = $"{path}\n\nAutoFile will rename the files already in this folder that don't follow the "
                   + $"{state.Config.Convention} convention, then keep new ones tidy too. Every rename can be undone from the History tab."
                   + "\n\nTo use a different convention here, choose Preview first, or select the folder afterwards and click Convention….",
            Buttons = { watch, preview, TaskDialogButton.Cancel },
            DefaultButton = watch,
            Verification = subfolders,
            Icon = TaskDialogIcon.Information,
        };

        var choice = TaskDialog.ShowDialog(this, page);
        if (choice == preview)
        {
            previewFolder(path, subfolders.Checked);
        }
        else if (choice == watch)
        {
            state.UpdateConfig(c =>
            {
                c.WatchedFolders.RemoveAll(f => f.Path.Equals(path, StringComparison.OrdinalIgnoreCase));
                c.WatchedFolders.Add(new WatchedFolder { Path = path, Recursive = subfolders.Checked });
            });
        }
    }

    void RemoveSelected()
    {
        if (Selected is { } folder)
        {
            state.UpdateConfig(c => c.WatchedFolders.RemoveAll(f => f.Path.Equals(folder.Path, StringComparison.OrdinalIgnoreCase)));
        }
    }

    void SetSubfolders(bool recursive)
    {
        if (refreshing || Selected is not { } folder)
        {
            return;
        }

        state.UpdateConfig(c =>
        {
            foreach (var match in c.WatchedFolders.Where(f => f.Path.Equals(folder.Path, StringComparison.OrdinalIgnoreCase)))
            {
                match.Recursive = recursive;
            }
        });
    }

    void ChangeFolderConvention()
    {
        if (Selected is not { } folder)
        {
            return;
        }

        using var dialog = new ConventionDialog($"Naming convention for {Path.GetFileName(folder.Path)}", folder.Convention, state.Config.Convention);
        if (dialog.ShowDialog(this) != DialogResult.OK
            || string.Equals(dialog.ChosenPattern, folder.Convention, StringComparison.Ordinal)
            || !ConfirmRename($"Files in {Path.GetFileName(folder.Path)} will be renamed to {dialog.ChosenPattern ?? state.Config.Convention}."))
        {
            return;
        }

        state.UpdateConfig(c =>
        {
            foreach (var match in c.WatchedFolders.Where(f => f.Path.Equals(folder.Path, StringComparison.OrdinalIgnoreCase)))
            {
                match.Convention = dialog.ChosenPattern;
            }
        });
    }

    void ChangeDefaultConvention()
    {
        using var dialog = new ConventionDialog("Default naming convention", state.Config.Convention, defaultPattern: null);
        if (dialog.ShowDialog(this) != DialogResult.OK || dialog.ChosenPattern is not { } pattern || pattern == state.Config.Convention)
        {
            return;
        }

        var affected = state.Config.WatchedFolders.Count(f => f.Convention == null);
        if (affected > 0
            && !ConfirmRename($"Files in {affected} watched {(affected == 1 ? "folder" : "folders")} that use the default will be renamed to {pattern}."))
        {
            return;
        }

        state.UpdateConfig(c => c.Convention = pattern);
    }

    bool ConfirmRename(string message) =>
        MessageBox.Show(this, message + "\n\nYou can undo this from the History tab.", "Change naming convention",
            MessageBoxButtons.OKCancel, MessageBoxIcon.Question) == DialogResult.OK;

    void OpenSelected()
    {
        if (Selected is { } folder && Directory.Exists(folder.Path))
        {
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{folder.Path}\"") { UseShellExecute = true });
        }
    }
}
