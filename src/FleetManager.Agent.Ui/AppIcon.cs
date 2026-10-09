using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using FleetManager.Agent.Core;

namespace FleetManager.Agent.Ui;

public static class AppIcon
{
    private const string ResourceName = "FleetManager.Agent.Ui.AppIcon.ico";

    /// <summary>The application icon resampled to <paramref name="size"/> with high-quality filtering.</summary>
    public static Bitmap Render(int size)
    {
        using var stream = typeof(AppIcon).Assembly.GetManifestResourceStream(ResourceName)
                           ?? throw new InvalidOperationException($"Resource {ResourceName} is missing.");
        using var icon = new Icon(stream, 256, 256);
        using var source = icon.ToBitmap();
        var bitmap = new Bitmap(size, size, PixelFormat.Format32bppArgb);
        using var graphics = Graphics.FromImage(bitmap);
        graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
        graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;
        graphics.CompositingQuality = CompositingQuality.HighQuality;
        graphics.DrawImage(source, new Rectangle(0, 0, size, size));
        return bitmap;
    }
}

/// <summary>
/// Tray icons per status: the plain app icon while everything is fine, a dot in the corner
/// (cut out of the icon, like Windows' own badges) when the user should take a look.
/// </summary>
public sealed class TrayIconSet : IDisposable
{
    private readonly Dictionary<StatusTone, (Icon Icon, IntPtr Handle)> _icons = new();

    public Icon Get(StatusTone tone)
    {
        var dot = Theme.TrayDot(tone);
        var key = dot is null ? StatusTone.Success : tone;
        if (_icons.TryGetValue(key, out var cached)) return cached.Icon;

        var size = SystemInformation.SmallIconSize.Width;
        using var bitmap = AppIcon.Render(size);
        if (dot is { } color) DrawDot(bitmap, color);
        var handle = bitmap.GetHicon();
        var icon = Icon.FromHandle(handle);
        _icons[key] = (icon, handle);
        return icon;
    }

    /// <summary>Drops cached icons, e.g. after a DPI change. Icons handed out earlier are destroyed.</summary>
    public void Reset()
    {
        foreach (var (icon, handle) in _icons.Values)
        {
            icon.Dispose();
            NativeMethods.DestroyIcon(handle);
        }
        _icons.Clear();
    }

    public void Dispose() => Reset();

    private static void DrawDot(Bitmap bitmap, Color color)
    {
        var size = bitmap.Width;
        var diameter = (float)Math.Round(size * 0.5);
        var ring = Math.Max(1f, size / 16f);
        var dot = new RectangleF(size - diameter, size - diameter, diameter, diameter);
        using var graphics = Graphics.FromImage(bitmap);
        graphics.SmoothingMode = SmoothingMode.AntiAlias;
        graphics.CompositingMode = CompositingMode.SourceCopy;
        using (var clear = new SolidBrush(Color.Transparent))
            graphics.FillEllipse(clear, RectangleF.Inflate(dot, ring, ring));
        graphics.CompositingMode = CompositingMode.SourceOver;
        using var fill = new SolidBrush(color);
        graphics.FillEllipse(fill, dot);
    }
}
