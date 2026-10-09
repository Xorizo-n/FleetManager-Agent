using System.Drawing.Drawing2D;
using FleetManager.Agent.Core;

namespace FleetManager.Agent.Ui;

/// <summary>
/// Windows 11 context menu look for ToolStrip menus: flat surface, rounded hover highlight
/// inset from the edges, hairline separators. Colours come from <see cref="Theme.Current"/>.
/// </summary>
public sealed class FluentMenuRenderer : ToolStripRenderer
{
    /// <summary>False once DWM rounds the popup and draws its border (Windows 11).</summary>
    public bool DrawBorder { get; set; } = true;

    /// <summary>Item-relative X of command text, as last laid out by the menu; 0 until first painted.</summary>
    public int TextLeft { get; private set; }

    /// <summary>Item-relative centre of the command icon column; 0 until first painted.</summary>
    public int ImageCenter { get; private set; }

    protected override void OnRenderToolStripBackground(ToolStripRenderEventArgs e) =>
        e.Graphics.Clear(Theme.Current.MenuBackground);

    protected override void OnRenderToolStripBorder(ToolStripRenderEventArgs e)
    {
        if (!DrawBorder) return;
        using var pen = new Pen(Theme.Current.MenuBorder);
        e.Graphics.DrawRectangle(pen, 0, 0, e.ToolStrip.Width - 1, e.ToolStrip.Height - 1);
    }

    protected override void OnRenderImageMargin(ToolStripRenderEventArgs e)
    {
    }

    protected override void OnRenderMenuItemBackground(ToolStripItemRenderEventArgs e)
    {
        var theme = Theme.Current;
        if (!e.Item.Enabled || !e.Item.Selected) return;
        var inset = Scale(e.Item, 4);
        var bounds = new RectangleF(inset, Scale(e.Item, 1), e.Item.Width - inset * 2, e.Item.Height - Scale(e.Item, 2));
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        using var path = Drawing.RoundedRect(bounds, Scale(e.Item, 4));
        using var brush = new SolidBrush(theme.MenuHover);
        e.Graphics.FillPath(brush, path);
    }

    protected override void OnRenderItemImage(ToolStripItemImageRenderEventArgs e)
    {
        Track(e.Item, image: e.ImageRectangle.X + e.ImageRectangle.Width / 2, text: TextLeft);
        base.OnRenderItemImage(e);
    }

    protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e)
    {
        var theme = Theme.Current;
        e.TextColor = !e.Item.Enabled ? theme.TextDisabled
            : theme.IsHighContrast && e.Item.Selected ? SystemColors.HighlightText
            : theme.TextPrimary;
        if (e.Item is ToolStripMenuItem) Track(e.Item, image: ImageCenter, text: e.TextRectangle.X);
        base.OnRenderItemText(e);
    }

    protected override void OnRenderSeparator(ToolStripSeparatorRenderEventArgs e)
    {
        using var pen = new Pen(Theme.Current.Divider);
        var y = e.Item.Height / 2;
        e.Graphics.DrawLine(pen, 0, y, e.Item.Width, y);
    }

    protected override void OnRenderArrow(ToolStripArrowRenderEventArgs e)
    {
        e.ArrowColor = Theme.Current.TextSecondary;
        base.OnRenderArrow(e);
    }

    // The header paints before the commands below it, so on the first paint it can only guess
    // where their text starts; once known, it is repainted in line with them.
    private void Track(ToolStripItem item, int image, int text)
    {
        if (image == ImageCenter && text == TextLeft) return;
        ImageCenter = image;
        TextLeft = text;
        if (item.Owner is not { } owner) return;
        foreach (var header in owner.Items.OfType<StatusMenuHeader>()) header.Invalidate();
    }

    private static int Scale(ToolStripItem item, int value) => item.Owner?.LogicalToDeviceUnits(value) ?? value;
}

/// <summary>
/// Non-interactive first row of the tray menu: a status dot in the icon column, the state
/// and one line of context aligned with the commands' text.
/// </summary>
public sealed class StatusMenuHeader : ToolStripMenuItem
{
    private StatusSummary _summary = new(StatusTone.Neutral, "Проверка состояния…");

    // Same padding as the commands' text (no NoPadding), so both start at the same pixel.
    // EndEllipsis only when drawing: with an empty proposed size it makes MeasureText return too little.
    private const TextFormatFlags MeasureFlags = TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine;
    private const TextFormatFlags DrawFlags = MeasureFlags | TextFormatFlags.EndEllipsis;

    public StatusMenuHeader()
    {
        AccessibleRole = AccessibleRole.StaticText;
        UpdateMeasureText();
    }

    public StatusSummary Summary
    {
        get => _summary;
        set
        {
            if (_summary == value) return;
            _summary = value;
            UpdateMeasureText();
            // The menu may need to grow for a longer detail line.
            Owner?.PerformLayout();
            Invalidate();
        }
    }

    public override bool CanSelect => false;

    public override Size GetPreferredSize(Size constrainingSize)
    {
        var title = Measure(_summary.Title, Typography.BodyStrong);
        var detail = Measure(_summary.Detail, Typography.Caption);
        // The drop-down takes each menu item's own preferred width; base gives the shared one.
        var width = Math.Max(base.GetPreferredSize(constrainingSize).Width, TextLeft() + Math.Max(title.Width, detail.Width) + Scale(20));
        return new Size(width, Scale(10) + ContentHeight(title, detail) + Scale(10));
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var theme = Theme.Current;
        var graphics = e.Graphics;
        var textLeft = TextLeft();
        var title = Measure(_summary.Title, Typography.BodyStrong);
        var detail = Measure(_summary.Detail, Typography.Caption);
        var top = (Height - ContentHeight(title, detail)) / 2;

        var dotSize = Scale(10);
        var center = Owner?.Renderer is FluentMenuRenderer { ImageCenter: > 0 } renderer ? renderer.ImageCenter : textLeft / 2;
        var dot = new RectangleF(center - dotSize / 2f, top + (title.Height - dotSize) / 2f, dotSize, dotSize);
        graphics.SmoothingMode = SmoothingMode.AntiAlias;
        using (var brush = new SolidBrush(theme.ToneColor(_summary.Tone)))
            graphics.FillEllipse(brush, dot);

        var width = Width - textLeft - Scale(12);
        TextRenderer.DrawText(graphics, _summary.Title, Typography.BodyStrong,
            new Rectangle(textLeft, top, width, title.Height), theme.TextPrimary, DrawFlags);
        if (detail.Height > 0)
        {
            TextRenderer.DrawText(graphics, _summary.Detail, Typography.Caption,
                new Rectangle(textLeft, top + title.Height + Scale(2), width, detail.Height), theme.TextSecondary, DrawFlags);
        }
    }

    private int TextLeft() =>
        Owner?.Renderer is FluentMenuRenderer { TextLeft: > 0 } renderer ? renderer.TextLeft : Scale(36);

    private int ContentHeight(Size title, Size detail) => title.Height + (detail.Height > 0 ? Scale(2) + detail.Height : 0);

    private static Size Measure(string? text, Font font) => string.IsNullOrEmpty(text)
        ? Size.Empty
        : TextRenderer.MeasureText(text, font, Size.Empty, MeasureFlags);

    // The drop-down sizes its text column from each menu item's Text and Font, not from
    // GetPreferredSize, so they carry the wider of the two lines. Only used for measuring.
    private void UpdateMeasureText()
    {
        var detailWider = Measure(_summary.Detail, Typography.Caption).Width > Measure(_summary.Title, Typography.BodyStrong).Width;
        Font = detailWider ? Typography.Caption : Typography.BodyStrong;
        Text = detailWider ? _summary.Detail : _summary.Title;
        AccessibleName = string.IsNullOrEmpty(_summary.Detail) ? _summary.Title : $"{_summary.Title}. {_summary.Detail}";
    }

    private int Scale(int value) => Owner?.LogicalToDeviceUnits(value) ?? value;
}
