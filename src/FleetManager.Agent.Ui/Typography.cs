namespace FleetManager.Agent.Ui;

/// <summary>
/// Windows 11 type ramp: Segoe UI Variable (Windows 11) with Segoe UI as the fallback.
/// Sizes follow WinUI — body 14 px, caption 12 px, subtitle 20 px.
/// </summary>
public static class Typography
{
    public static Font Body { get; } = Create(10.5f, FontStyle.Regular, "Segoe UI Variable Text", "Segoe UI");
    public static Font BodyStrong { get; } = Create(10.5f, FontStyle.Regular, "Segoe UI Variable Text Semibold", "Segoe UI Semibold");
    public static Font Caption { get; } = Create(9f, FontStyle.Regular, "Segoe UI Variable Small", "Segoe UI");
    public static Font Subtitle { get; } = Create(15f, FontStyle.Regular, "Segoe UI Variable Display Semibold", "Segoe UI Semibold");
    public static Font Monospace { get; } = Create(9.75f, FontStyle.Regular, "Cascadia Mono", "Consolas");

    /// <summary>Segoe Fluent Icons on Windows 11, Segoe MDL2 Assets on Windows 10 (same code points).</summary>
    public static string IconFamily { get; } = Resolve("Segoe Fluent Icons", "Segoe MDL2 Assets");

    /// <summary>Icon font in pixels, so glyphs keep their size regardless of the DC's DPI.</summary>
    public static Font Icons(int pixelSize) => new(IconFamily, pixelSize, FontStyle.Regular, GraphicsUnit.Pixel);

    private static Font Create(float size, FontStyle style, params string[] families) =>
        new(Resolve(families), size, style, GraphicsUnit.Point);

    // Font silently falls back to Microsoft Sans Serif for an unknown family; check Name instead.
    private static string Resolve(params string[] families)
    {
        foreach (var family in families)
        {
            using var probe = new Font(family, 10f);
            if (string.Equals(probe.Name, family, StringComparison.OrdinalIgnoreCase)) return family;
        }
        return families[^1];
    }
}

/// <summary>Code points shared by Segoe Fluent Icons and Segoe MDL2 Assets.</summary>
public static class Glyphs
{
    public const string Accept = "";
    public const string Cancel = "";
    public const string Important = "";
    public const string Clock = "";
    public const string Sync = "";
    public const string Refresh = "";
    public const string Settings = "";
    public const string Globe = "";
    public const string Computer = "";
    public const string Document = "";
    public const string FolderOpen = "";
    public const string Save = "";
    public const string Copy = "";
    public const string Hide = "";
    public const string Play = "";
}
