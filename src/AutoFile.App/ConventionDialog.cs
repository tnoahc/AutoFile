namespace AutoFile.App;

/// <summary>Chooses a naming convention, optionally with a "use the default" choice (for a watched folder).</summary>
sealed class ConventionDialog : Form
{
    readonly RadioButton? useDefault;
    readonly RadioButton useCustom;
    readonly ConventionPicker picker = new() { Margin = new Padding(20, 3, 3, 3) };
    readonly Button okButton = new() { Text = "OK", AutoSize = true, DialogResult = DialogResult.OK };

    /// <param name="heading">What is being changed, e.g. "Naming convention for Invoices".</param>
    /// <param name="currentPattern">The convention set now, or null if it follows the default.</param>
    /// <param name="defaultPattern">The default convention; null when editing the default itself.</param>
    public ConventionDialog(string heading, string? currentPattern, string? defaultPattern)
    {
        Text = "Naming convention";
        Icon = AppIcon.Large;
        Font = SystemFonts.MessageBoxFont ?? Font;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        StartPosition = FormStartPosition.CenterParent;
        MaximizeBox = false;
        MinimizeBox = false;
        ShowInTaskbar = false;
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;
        Padding = new Padding(LogicalToDeviceUnits(12));

        var layout = new TableLayoutPanel { AutoSize = true, ColumnCount = 1, Dock = DockStyle.Fill };
        layout.Controls.Add(new Label
        {
            Text = heading,
            AutoSize = true,
            Font = new Font(Font.FontFamily, Font.Size * 1.15f, FontStyle.Bold),
            Margin = new Padding(3, 3, 3, LogicalToDeviceUnits(8)),
        });

        if (defaultPattern != null)
        {
            useDefault = new RadioButton { Text = $"Use the default convention ({defaultPattern})", AutoSize = true, Checked = currentPattern == null };
            layout.Controls.Add(useDefault);
        }

        useCustom = new RadioButton
        {
            Text = "Use this convention:",
            AutoSize = true,
            Checked = defaultPattern == null || currentPattern != null,
        };

        // With no "use the default" choice, a lone radio button would look odd, so show a label instead.
        layout.Controls.Add(defaultPattern != null ? useCustom : new Label { Text = "Convention:", AutoSize = true });
        layout.Controls.Add(picker);
        layout.Controls.Add(new Label
        {
            Text = "Pick a preset, or type your own using YYYY or YY, MM, DD and the word Title,\nfor example: Title - DD.MM.YYYY",
            AutoSize = true,
            ForeColor = SystemColors.GrayText,
            Margin = new Padding(LogicalToDeviceUnits(20), 3, 3, LogicalToDeviceUnits(12)),
        });

        var cancelButton = new Button { Text = "Cancel", AutoSize = true, DialogResult = DialogResult.Cancel };
        var buttons = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.RightToLeft, Dock = DockStyle.Fill, WrapContents = false };
        buttons.Controls.AddRange([cancelButton, okButton]);
        layout.Controls.Add(buttons);
        Controls.Add(layout);
        AcceptButton = okButton;
        CancelButton = cancelButton;

        picker.Pattern = currentPattern ?? defaultPattern ?? AutoFile.Core.NamingConvention.DefaultPattern;
        picker.Edited += UpdateState;
        useCustom.CheckedChanged += (_, _) => UpdateState();
        UpdateState();
    }

    /// <summary>The chosen convention, or null for "use the default".</summary>
    public string? ChosenPattern => useDefault?.Checked == true ? null : picker.Convention?.Pattern;

    void UpdateState()
    {
        picker.Enabled = useCustom.Checked;
        okButton.Enabled = !useCustom.Checked || picker.Convention != null;
    }
}
