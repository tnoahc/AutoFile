using AutoFile.Core;

namespace AutoFile.App;

/// <summary>Pick a folder, see which files would be renamed, untick any to leave alone, and rename.</summary>
sealed class RenamePage : UserControl
{
    readonly AppState state;
    readonly TextBox folderBox = new() { Dock = DockStyle.Fill, PlaceholderText = "Choose a folder, or drag one here" };
    readonly Button browseButton = new() { Text = "Browse…", AutoSize = true };
    readonly CheckBox recursiveBox = new() { Text = "Include subfolders", AutoSize = true, Anchor = AnchorStyles.Left };
    readonly Button previewButton = new() { Text = "Check folder", AutoSize = true };
    readonly ConventionPicker conventionPicker = new() { Anchor = AnchorStyles.Left, Margin = Padding.Empty };
    readonly ListView list = new()
    {
        Dock = DockStyle.Fill,
        View = View.Details,
        CheckBoxes = true,
        FullRowSelect = true,
        HideSelection = false,
    };
    readonly Label statusLabel = new() { Dock = DockStyle.Fill, AutoEllipsis = true, TextAlign = ContentAlignment.MiddleLeft };
    readonly Button renameButton = new() { Text = "Rename", AutoSize = true, Enabled = false };
    readonly Button undoButton = new() { Text = "Undo", AutoSize = true, Visible = false };
    string? previewedFolder;
    string? lastBatchId;
    bool populating;

    public RenamePage(AppState state)
    {
        this.state = state;

        list.Columns.Add("Current name", 280);
        list.Columns.Add("New name", 280);
        list.Columns.Add("Date taken from", 140);

        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 3, RowCount = 5 };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));

        layout.Controls.Add(new Label { Text = "Folder:", AutoSize = true, Anchor = AnchorStyles.Left }, 0, 0);
        layout.Controls.Add(folderBox, 1, 0);
        layout.Controls.Add(browseButton, 2, 0);
        layout.Controls.Add(new Label { Text = "Name as:", AutoSize = true, Anchor = AnchorStyles.Left }, 0, 1);
        layout.Controls.Add(conventionPicker, 1, 1);
        layout.Controls.Add(recursiveBox, 1, 2);
        layout.Controls.Add(previewButton, 2, 2);
        layout.Controls.Add(list, 0, 3);
        layout.SetColumnSpan(list, 3);

        var actions = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.RightToLeft, WrapContents = false, Margin = Padding.Empty };
        actions.Controls.Add(renameButton);
        actions.Controls.Add(undoButton);
        layout.Controls.Add(statusLabel, 0, 4);
        layout.SetColumnSpan(statusLabel, 2);
        layout.Controls.Add(actions, 2, 4);
        Controls.Add(layout);

        conventionPicker.Pattern = state.Config.Convention;
        conventionPicker.ConventionChanged += async () =>
        {
            if (conventionPicker.Convention == null)
            {
                ShowConventionError();
            }
            else if (previewedFolder != null)
            {
                await PreviewAsync();
            }
        };

        browseButton.Click += (_, _) => Browse();
        previewButton.Click += async (_, _) => await PreviewAsync();
        recursiveBox.CheckedChanged += async (_, _) =>
        {
            if (previewedFolder != null)
            {
                await PreviewAsync();
            }
        };
        folderBox.KeyDown += async (_, e) =>
        {
            if (e.KeyCode == Keys.Enter)
            {
                e.SuppressKeyPress = true;
                await PreviewAsync();
            }
        };
        list.ItemChecked += (_, _) => UpdateRenameButton();
        renameButton.Click += async (_, _) => await RenameCheckedAsync();
        undoButton.Click += async (_, _) => await UndoAsync();

        AllowDrop = true;
        list.AllowDrop = true;
        foreach (var target in new Control[] { this, list, folderBox })
        {
            target.DragEnter += (_, e) => e.Effect = DroppedFolder(e) != null ? DragDropEffects.Link : DragDropEffects.None;
            target.DragDrop += (_, e) =>
            {
                if (DroppedFolder(e) is { } folder)
                {
                    PreviewFolder(folder, recursiveBox.Checked);
                }
            };
        }

        SetStatus("Choose a folder to see which files don't follow your naming convention.");
    }

    public async void PreviewFolder(string folder, bool recursive)
    {
        folderBox.Text = folder;
        populating = true;
        recursiveBox.Checked = recursive;

        // A watched folder brings its own convention; for any other folder keep whatever is chosen.
        if (state.Config.WatchedFolderFor(folder) is { } watched)
        {
            conventionPicker.Pattern = watched.Convention ?? state.Config.Convention;
        }

        populating = false;
        await PreviewAsync();
    }

    void ShowConventionError()
    {
        list.Items.Clear();
        UpdateRenameButton();
        SetStatus($"Check the convention: {conventionPicker.Error}");
    }

    void Browse()
    {
        using var dialog = new FolderBrowserDialog
        {
            Description = "Choose a folder to tidy",
            UseDescriptionForTitle = true,
            InitialDirectory = Directory.Exists(folderBox.Text) ? folderBox.Text : "",
        };
        if (dialog.ShowDialog(this) == DialogResult.OK)
        {
            PreviewFolder(dialog.SelectedPath, recursiveBox.Checked);
        }
    }

    async Task PreviewAsync()
    {
        if (populating)
        {
            return;
        }

        var folder = folderBox.Text.Trim().Trim('"');
        if (!Directory.Exists(folder))
        {
            list.Items.Clear();
            previewedFolder = null;
            UpdateRenameButton();
            SetStatus(folder.Length == 0 ? "Choose a folder first." : "That folder doesn't exist.");
            return;
        }

        if (conventionPicker.Convention is not { } convention)
        {
            ShowConventionError();
            return;
        }

        SetBusy(true, "Checking files…");
        try
        {
            var planner = new RenamePlanner(state.Config, [AppPaths.DataDirectory], convention);
            var recursive = recursiveBox.Checked;
            var plans = await Task.Run(() => planner.PlanFolder(folder, recursive));
            Populate(folder, plans);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            list.Items.Clear();
            SetStatus($"Couldn't read the folder: {ex.Message}");
        }
        finally
        {
            SetBusy(false);
        }
    }

    void Populate(string folder, IReadOnlyList<PlannedRename> plans)
    {
        previewedFolder = folder;
        populating = true;
        list.BeginUpdate();
        list.Items.Clear();
        foreach (var plan in plans)
        {
            var item = new ListViewItem(Path.GetRelativePath(folder, plan.SourcePath)) { Checked = true, Tag = plan };
            item.SubItems.Add(plan.TargetName);
            item.SubItems.Add(Describe(plan.Source));
            list.Items.Add(item);
        }

        if (plans.Count > 0)
        {
            list.AutoResizeColumns(ColumnHeaderAutoResizeStyle.ColumnContent);
            foreach (ColumnHeader column in list.Columns)
            {
                column.Width = Math.Max(column.Width + LogicalToDeviceUnits(16), LogicalToDeviceUnits(140));
            }
        }

        list.EndUpdate();
        populating = false;

        SetStatus(plans.Count == 0
            ? $"Every file here already follows {conventionPicker.Convention}."
            : $"{plans.Count} {Plural(plans.Count)} don't follow {conventionPicker.Convention}. Untick any you want to leave as they are.");
        UpdateRenameButton();
    }

    async Task RenameCheckedAsync()
    {
        var plans = list.CheckedItems.Cast<ListViewItem>().Select(i => (PlannedRename)i.Tag!).ToList();
        if (plans.Count == 0)
        {
            return;
        }

        SetBusy(true, "Renaming…");
        IReadOnlyList<RenameResult> results;
        RenameBatch? batch;
        try
        {
            (results, batch) = await Task.Run(() => RenameExecutor.ApplyAndRecord(plans, state.Journal, "app"));
        }
        catch (Exception ex) when (ex is IOException or TimeoutException or LibGit2Sharp.LibGit2SharpException)
        {
            SetBusy(false);
            MessageBox.Show(this, $"Renaming stopped: {ex.Message}", "AutoFile", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return;
        }

        SetBusy(false);
        state.NotifyJournalChanged();
        lastBatchId = batch?.Id;
        undoButton.Visible = batch != null;

        var failures = results.Where(r => !r.Succeeded).ToList();
        if (failures.Count > 0)
        {
            var details = string.Join("\n", failures.Take(10).Select(f => $"{f.Plan.SourceName}: {f.Error}"));
            MessageBox.Show(this, $"{failures.Count} {Plural(failures.Count)} couldn't be renamed:\n\n{details}", "AutoFile",
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }

        await PreviewAsync();
        var renamed = results.Count - failures.Count;
        SetStatus($"Renamed {renamed} {Plural(renamed)}." + (list.Items.Count > 0 ? $" {list.Items.Count} left unchanged." : ""));
    }

    async Task UndoAsync()
    {
        if (lastBatchId == null)
        {
            return;
        }

        try
        {
            var outcome = state.Journal.Undo(lastBatchId);
            state.NotifyJournalChanged();
            if (outcome.Problems.Count > 0)
            {
                MessageBox.Show(this, string.Join("\n", outcome.Problems.Take(10)), "Some names couldn't be restored",
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or TimeoutException)
        {
            MessageBox.Show(this, ex.Message, "AutoFile", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }

        lastBatchId = null;
        undoButton.Visible = false;
        await PreviewAsync();
        SetStatus("Original names restored.");
    }

    void UpdateRenameButton()
    {
        if (populating)
        {
            return;
        }

        var count = list.CheckedItems.Count;
        renameButton.Enabled = count > 0;
        renameButton.Text = count > 0 ? $"Rename {count} {Plural(count)}" : "Rename";
    }

    void SetBusy(bool busy, string? message = null)
    {
        UseWaitCursor = busy;
        foreach (var control in new Control[] { folderBox, browseButton, conventionPicker, recursiveBox, previewButton, list, renameButton, undoButton })
        {
            control.Enabled = !busy;
        }

        if (!busy)
        {
            UpdateRenameButton();
        }

        if (message != null)
        {
            SetStatus(message);
        }
    }

    void SetStatus(string text) => statusLabel.Text = text;

    static string? DroppedFolder(DragEventArgs e) =>
        e.Data?.GetData(DataFormats.FileDrop) is string[] { Length: > 0 } paths && Directory.Exists(paths[0]) ? paths[0] : null;

    static string Describe(DateSource source) => source switch
    {
        DateSource.FileName => "Name",
        DateSource.Metadata => "File properties",
        _ => "Created/modified",
    };

    static string Plural(int count) => count == 1 ? "file" : "files";
}
