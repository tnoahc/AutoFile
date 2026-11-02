using System.Diagnostics;
using AutoFile.Core;

namespace AutoFile.App;

/// <summary>Every batch of renames from the git journal, with the files in it and an undo button.</summary>
sealed class HistoryPage : UserControl
{
    const int MaxBatches = 500;

    readonly AppState state;
    readonly ListView batches = new()
    {
        Dock = DockStyle.Fill,
        View = View.Details,
        FullRowSelect = true,
        HideSelection = false,
        MultiSelect = false,
    };
    readonly ListView entries = new()
    {
        Dock = DockStyle.Fill,
        View = View.Details,
        FullRowSelect = true,
    };
    readonly Button undoButton = new() { Text = "Undo this batch", AutoSize = true, Enabled = false };
    readonly Button openJournalButton = new() { Text = "Open journal folder", AutoSize = true };
    HashSet<string> undone = [];
    bool stale = true;

    public HistoryPage(AppState state)
    {
        this.state = state;

        batches.Columns.Add("When", 190);
        batches.Columns.Add("What happened", 260);
        batches.Columns.Add("Files", 60, HorizontalAlignment.Right);
        batches.Columns.Add("Status", 100);
        entries.Columns.Add("Before", 280);
        entries.Columns.Add("After", 280);
        entries.Columns.Add("Folder", 260);
        foreach (var column in batches.Columns.Cast<ColumnHeader>().Concat(entries.Columns.Cast<ColumnHeader>()))
        {
            column.Width = LogicalToDeviceUnits(column.Width);
        }

        var split = new SplitContainer { Dock = DockStyle.Fill, Orientation = Orientation.Horizontal };
        split.Panel1.Controls.Add(batches);
        split.Panel2.Controls.Add(entries);

        var actions = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, FlowDirection = FlowDirection.RightToLeft, WrapContents = false };
        actions.Controls.AddRange([undoButton, openJournalButton]);

        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2 };
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.Controls.Add(split, 0, 0);
        layout.Controls.Add(actions, 0, 1);
        Controls.Add(layout);

        batches.SelectedIndexChanged += (_, _) => ShowSelected();
        undoButton.Click += (_, _) => UndoSelected();
        openJournalButton.Click += (_, _) =>
        {
            Directory.CreateDirectory(state.Journal.Location);
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{state.Journal.Location}\"") { UseShellExecute = true });
        };

        // Reload lazily: only when the tab is visible, so background renames don't do extra work.
        state.JournalChanged += OnJournalChanged;
        Disposed += (_, _) => state.JournalChanged -= OnJournalChanged;
        VisibleChanged += (_, _) =>
        {
            if (Visible && stale)
            {
                Reload();
            }
        };
        var splitPlaced = false;
        split.SizeChanged += (_, _) =>
        {
            // Wait for a real size; early layout passes can be only a few pixels tall.
            if (!splitPlaced && split.Height >= split.Panel1MinSize + split.Panel2MinSize + split.SplitterWidth + LogicalToDeviceUnits(100))
            {
                splitPlaced = true;
                split.SplitterDistance = split.Height / 2;
            }
        };
    }

    void OnJournalChanged()
    {
        stale = true;
        if (Visible)
        {
            Reload();
        }
    }

    RenameBatch? Selected => batches.SelectedItems.Count == 1 ? (RenameBatch)batches.SelectedItems[0].Tag! : null;

    void Reload()
    {
        stale = false;
        var selectedId = Selected?.Id;
        IReadOnlyList<RenameBatch> all;
        try
        {
            all = state.Journal.Batches();
        }
        catch (Exception ex) when (ex is IOException or System.Text.Json.JsonException)
        {
            batches.Items.Clear();
            batches.Items.Add(new ListViewItem($"Couldn't read history: {ex.Message}"));
            return;
        }

        undone = all.Where(b => b.UndoOf != null).Select(b => b.UndoOf!).ToHashSet();

        batches.BeginUpdate();
        batches.Items.Clear();
        foreach (var batch in all.Take(MaxBatches))
        {
            var item = new ListViewItem(batch.Timestamp.LocalDateTime.ToString("ddd d MMM yyyy  HH:mm")) { Tag = batch };
            item.SubItems.Add(Describe(batch));
            item.SubItems.Add(batch.Entries.Count.ToString());
            item.SubItems.Add(batch.UndoOf != null ? "" : undone.Contains(batch.Id) ? "Undone" : "");
            if (undone.Contains(batch.Id) || batch.UndoOf != null)
            {
                item.ForeColor = SystemColors.GrayText;
            }

            batches.Items.Add(item);
            item.Selected = batch.Id == selectedId;
        }

        if (all.Count == 0)
        {
            batches.Items.Add(new ListViewItem("Nothing has been renamed yet.") { ForeColor = SystemColors.GrayText });
        }

        if (batches.SelectedItems.Count == 0 && all.Count > 0)
        {
            batches.Items[0].Selected = true;
        }

        batches.EndUpdate();
        ShowSelected();
    }

    void ShowSelected()
    {
        var batch = Selected;
        entries.BeginUpdate();
        entries.Items.Clear();
        foreach (var entry in batch?.Entries ?? [])
        {
            var item = new ListViewItem(Path.GetFileName(entry.OldPath));
            item.SubItems.Add(Path.GetFileName(entry.NewPath));
            item.SubItems.Add(Path.GetDirectoryName(entry.NewPath));
            entries.Items.Add(item);
        }

        entries.EndUpdate();
        undoButton.Enabled = batch != null && batch.UndoOf == null && !undone.Contains(batch.Id);
    }

    void UndoSelected()
    {
        if (Selected is not { } batch)
        {
            return;
        }

        var count = batch.Entries.Count;
        if (MessageBox.Show(this, $"Put {count} {(count == 1 ? "file" : "files")} back to {(count == 1 ? "its" : "their")} original {(count == 1 ? "name" : "names")}?",
                "Undo", MessageBoxButtons.OKCancel, MessageBoxIcon.Question) != DialogResult.OK)
        {
            return;
        }

        try
        {
            var outcome = state.Journal.Undo(batch.Id);
            if (outcome.Problems.Count > 0)
            {
                MessageBox.Show(this, string.Join("\n", outcome.Problems.Take(10)), "Some names couldn't be restored",
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or TimeoutException)
        {
            MessageBox.Show(this, ex.Message, "Undo", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }

        state.NotifyJournalChanged();
    }

    static string Describe(RenameBatch batch) => batch.Origin switch
    {
        "undo" => "Undid an earlier batch",
        "watch" => "Renamed automatically (watched folder)",
        "scan" => "Renamed from the command line",
        _ => "Renamed in AutoFile",
    };
}
