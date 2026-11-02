namespace AutoFile.App;

sealed class MainWindow : Form
{
    public enum Page
    {
        Rename,
        Watched,
        History,
    }

    readonly TabControl tabs = new() { Dock = DockStyle.Fill };
    readonly RenamePage renamePage;
    readonly WatchPage watchPage;
    readonly HistoryPage historyPage;

    public MainWindow(AppState state)
    {
        Text = "AutoFile";
        Icon = AppIcon.Large;
        Font = SystemFonts.MessageBoxFont ?? Font;
        StartPosition = FormStartPosition.CenterScreen;
        ClientSize = new Size(LogicalToDeviceUnits(860), LogicalToDeviceUnits(560));
        MinimumSize = new Size(LogicalToDeviceUnits(620), LogicalToDeviceUnits(420));

        renamePage = new RenamePage(state) { Dock = DockStyle.Fill };
        historyPage = new HistoryPage(state) { Dock = DockStyle.Fill };
        watchPage = new WatchPage(state, (folder, recursive) =>
        {
            ShowPage(Page.Rename);
            PreviewFolder(folder, recursive);
        }) { Dock = DockStyle.Fill };

        AddTab("Rename files", renamePage);
        AddTab("Watched folders", watchPage);
        AddTab("History", historyPage);
        Controls.Add(tabs);
    }

    public void ShowPage(Page page) => tabs.SelectedIndex = (int)page;

    public void PreviewFolder(string folder, bool recursive) => renamePage.PreviewFolder(folder, recursive);

    void AddTab(string title, Control content)
    {
        var tab = new TabPage(title) { Padding = new Padding(LogicalToDeviceUnits(10)) };
        tab.Controls.Add(content);
        tabs.TabPages.Add(tab);
    }
}
