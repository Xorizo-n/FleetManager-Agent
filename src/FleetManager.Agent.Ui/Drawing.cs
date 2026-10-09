using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using FleetManager.Agent.Core;

namespace FleetManager.Agent.Ui;

public static class Drawing
{
    public static GraphicsPath RoundedRect(RectangleF bounds, float radius)
    {
        var path = new GraphicsPath();
        var diameter = Math.Min(radius * 2, Math.Min(bounds.Width, bounds.Height));
        if (diameter <= 0)
        {
            path.AddRectangle(bounds);
            return path;
        }
        var arc = new RectangleF(bounds.Location, new SizeF(diameter, diameter));
        path.AddArc(arc, 180, 90);
        arc.X = bounds.Right - diameter;
        path.AddArc(arc, 270, 90);
        arc.Y = bounds.Bottom - diameter;
        path.AddArc(arc, 0, 90);
        arc.X = bounds.Left;
        path.AddArc(arc, 90, 90);
        path.CloseFigure();
        return path;
    }

    /// <summary><paramref name="top"/> at <paramref name="opacity"/> over an opaque <paramref name="bottom"/>.</summary>
    public static Color Blend(Color top, Color bottom, double opacity) => Color.FromArgb(
        (int)Math.Round(top.R * opacity + bottom.R * (1 - opacity)),
        (int)Math.Round(top.G * opacity + bottom.G * (1 - opacity)),
        (int)Math.Round(top.B * opacity + bottom.B * (1 - opacity)));

    /// <summary>A Fluent icon glyph on a transparent bitmap, for menu items.</summary>
    public static Bitmap GlyphBitmap(string glyph, Color color, int pixelSize)
    {
        var bitmap = new Bitmap(pixelSize, pixelSize, PixelFormat.Format32bppArgb);
        using var graphics = Graphics.FromImage(bitmap);
        using var font = Typography.Icons(pixelSize);
        using var brush = new SolidBrush(color);
        graphics.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
        DrawCentered(graphics, glyph, font, brush, new RectangleF(0, 0, pixelSize, pixelSize));
        return bitmap;
    }

    /// <summary>
    /// Glyph centred in <paramref name="bounds"/>. Not GenericTypographic: it carries LineLimit,
    /// and a glyph whose line box is as tall as the bounds is then not drawn at all.
    /// </summary>
    public static void DrawCentered(Graphics graphics, string glyph, Font font, Brush brush, RectangleF bounds)
    {
        using var format = new StringFormat(StringFormatFlags.NoWrap | StringFormatFlags.NoClip)
        {
            Alignment = StringAlignment.Center,
            LineAlignment = StringAlignment.Center
        };
        graphics.DrawString(glyph, font, brush, bounds, format);
    }

    /// <summary>Status circle with its glyph: a check, "!" or "×" in the tone colour.</summary>
    public static void DrawStatusBadge(Graphics graphics, RectangleF bounds, StatusTone tone, Theme theme)
    {
        var smoothing = graphics.SmoothingMode;
        var hint = graphics.TextRenderingHint;
        graphics.SmoothingMode = SmoothingMode.AntiAlias;
        graphics.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
        using (var fill = new SolidBrush(theme.ToneColor(tone)))
            graphics.FillEllipse(fill, bounds);
        var glyph = tone switch
        {
            StatusTone.Success => Glyphs.Accept,
            StatusTone.Warning => Glyphs.Important,
            StatusTone.Error => Glyphs.Cancel,
            StatusTone.Busy => Glyphs.Sync,
            _ => Glyphs.Clock
        };
        using (var font = Typography.Icons(Math.Max(6, (int)Math.Round(bounds.Height * 0.5))))
        using (var brush = new SolidBrush(theme.OnTone))
            DrawCentered(graphics, glyph, font, brush, bounds);
        graphics.SmoothingMode = smoothing;
        graphics.TextRenderingHint = hint;
    }
}
