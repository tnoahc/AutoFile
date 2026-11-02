using AutoFile.Core;

namespace AutoFile.App;

/// <summary>
/// An editable drop-down of naming conventions with a live example ("e.g. 251102 Budget Review.docx")
/// or, when what's typed isn't valid, the reason why.
/// </summary>
sealed class ConventionPicker : UserControl
{
    readonly ComboBox box = new() { DropDownStyle = ComboBoxStyle.DropDown };
    readonly Label example = new() { AutoSize = true, Anchor = AnchorStyles.Left };
    readonly System.Windows.Forms.Timer typingTimer = new() { Interval = 600 };
    readonly ToolTip tip = new();
    string? committedPattern;
    bool silent;

    public ConventionPicker()
    {
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;
        box.Items.AddRange([.. NamingConvention.Presets]);
        box.Width = LogicalToDeviceUnits(190);

        var layout = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Dock = DockStyle.Fill, Margin = Padding.Empty };
        layout.Controls.Add(box);
        layout.Controls.Add(example);
        Controls.Add(layout);

        tip.SetToolTip(box, "Pick a preset, or type your own using YYYY or YY, MM, DD and the word Title.\nFor example: Title - DD.MM.YYYY");
        box.SelectedIndexChanged += (_, _) =>
        {
            if (!silent)
            {
                Commit();
            }
        };
        box.TextChanged += (_, _) =>
        {
            if (silent)
            {
                return;
            }

            Check();
            typingTimer.Stop();
            typingTimer.Start();
        };
        box.Leave += (_, _) => Commit();
        typingTimer.Tick += (_, _) => Commit();
        Disposed += (_, _) =>
        {
            typingTimer.Dispose();
            tip.Dispose();
        };

        Pattern = NamingConvention.DefaultPattern;
    }

    /// <summary>The convention as typed, or null while it isn't valid.</summary>
    public NamingConvention? Convention { get; private set; }

    public string? Error { get; private set; }

    /// <summary>Raised on every edit, once the text has been checked.</summary>
    public event Action? Edited;

    /// <summary>Raised when a different convention has been chosen (after a short pause in typing).</summary>
    public event Action? ConventionChanged;

    /// <summary>Sets the text without raising <see cref="ConventionChanged"/>.</summary>
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public string Pattern
    {
        get => box.Text;
        set
        {
            silent = true;
            box.Text = value;
            silent = false;
            Check();
            committedPattern = Convention?.Pattern;
        }
    }

    void Check()
    {
        if (NamingConvention.TryParse(box.Text, out var convention, out var error))
        {
            Convention = convention;
            Error = null;
            example.Text = $"e.g. {convention.Example(DateOnly.FromDateTime(DateTime.Today))}";
            example.ForeColor = SystemColors.GrayText;
        }
        else
        {
            Convention = null;
            Error = error;
            example.Text = error;
            example.ForeColor = Color.Firebrick;
        }

        Edited?.Invoke();
    }

    void Commit()
    {
        typingTimer.Stop();
        Check();
        var pattern = Convention?.Pattern;
        if (!string.Equals(pattern, committedPattern, StringComparison.Ordinal))
        {
            committedPattern = pattern;
            ConventionChanged?.Invoke();
        }
    }
}
