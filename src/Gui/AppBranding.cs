namespace AutoFollower.Gui;

internal static class AppBranding
{
    private static readonly Icon? SharedIcon = LoadIcon();

    internal static void Apply(Form form)
    {
        if (SharedIcon is not null) form.Icon = SharedIcon;
    }

    private static Icon? LoadIcon()
    {
        try
        {
            using var stream = typeof(AppBranding).Assembly.GetManifestResourceStream("AutoFollower.AppIcon.ico");
            if (stream is not null) return new Icon(stream);
            return Icon.ExtractAssociatedIcon(Application.ExecutablePath);
        }
        catch (Exception e) when (e is ArgumentException or IOException or System.ComponentModel.Win32Exception) { return null; }
    }
}
