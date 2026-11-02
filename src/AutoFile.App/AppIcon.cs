namespace AutoFile.App;

static class AppIcon
{
    public static Icon Large { get; } = Load(SystemInformation.IconSize);

    public static Icon Small { get; } = Load(SystemInformation.SmallIconSize);

    static Icon Load(Size size)
    {
        using var stream = typeof(AppIcon).Assembly.GetManifestResourceStream("AutoFile.App.app.ico")!;
        return new Icon(stream, size);
    }
}
