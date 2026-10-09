using System.ComponentModel;
using System.Diagnostics.CodeAnalysis;
using System.Drawing.Drawing2D;
using FleetManager.Agent.Core;

namespace FleetManager.Agent.Ui;

/// <summary>Rounded card on the window background (Windows 11 settings card).</summary>
public class CardPanel : Panel
{
    public CardPanel()
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        Padding = new Padding(16, 14, 16, 14);
    }

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public Color BorderColor { get; set; } = Color.Transparent;

    protected override void OnPaintBackground(PaintEventArgs e)
    {
        var graphics = e.Graphics;
        graphics.Clear(Parent?.BackColor ?? BackColor);
        graphics.SmoothingMode = SmoothingMode.AntiAlias;
        using var path = Drawing.RoundedRect(new RectangleF(0.5f, 0.5f, Width - 1f, Height - 1f), LogicalToDeviceUnits(8));
        using (var fill = new SolidBrush(BackColor))
            graphics.FillPath(fill, path);
        using var pen = new Pen(BorderColor);
        graphics.DrawPath(pen, path);
    }
}

/// <summary>Windows 11 button: standard (light fill, elevation edge) or accent, with an optional Fluent glyph.</summary>
public sealed class FluentButton : Button
{
    private bool _hover;
    private bool _pressed;
    private bool _accent;
    private string? _glyph;

    public FluentButton()
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;
        Font = Typography.Body;
        UseVisualStyleBackColor = false;
    }

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public bool IsAccent
    {
        get => _accent;
        set { _accent = value; Invalidate(); }
    }

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public string? Glyph
    {
        get => _glyph;
        set { _glyph = value; PerformLayout(); Invalidate(); }
    }

    public override Size GetPreferredSize(Size proposedSize)
    {
        var text = string.IsNullOrEmpty(Text) ? Size.Empty : TextRenderer.MeasureText(Text, Font, Size.Empty, TextFormatFlags.NoPadding);
        var glyph = string.IsNullOrEmpty(_glyph) ? 0 : LogicalToDeviceUnits(16) + (text.Width > 0 ? LogicalToDeviceUnits(8) : 0);
        var width = LogicalToDeviceUnits(12) * 2 + glyph + text.Width;
        return new Size(Math.Max(width, LogicalToDeviceUnits(32)), LogicalToDeviceUnits(32));
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var theme = Theme.Current;
        var graphics = e.Graphics;
        graphics.Clear(Parent?.BackColor ?? theme.Card);
        graphics.SmoothingMode = SmoothingMode.AntiAlias;

        Color fill, text;
        if (_accent)
        {
            fill = !Enabled ? theme.AccentDisabled : _pressed ? theme.AccentPressed : _hover ? theme.AccentHover : theme.Accent;
            text = Enabled ? theme.OnAccent : theme.IsDark ? theme.TextDisabled : Color.White;
        }
        else
        {
            fill = !Enabled ? theme.ControlFillDisabled : _pressed ? theme.ControlFillPressed : _hover ? theme.ControlFillHover : theme.ControlFill;
            text = !Enabled ? theme.TextDisabled : _pressed ? theme.TextSecondary : theme.TextPrimary;
        }

        var bounds = new RectangleF(0.5f, 0.5f, Width - 1f, Height - 1f);
        var radius = LogicalToDeviceUnits(4);
        using (var path = Drawing.RoundedRect(bounds, radius))
        {
            using (var brush = new SolidBrush(fill))
                graphics.FillPath(brush, path);
            if (!_accent || theme.IsHighContrast)
            {
                using (var pen = new Pen(theme.ControlBorder))
                    graphics.DrawPath(pen, path);
                if (Enabled && !_pressed && theme.ControlBorderBottom != theme.ControlBorder)
                {
                    // Elevation: only the bottom edge of the outline is darker.
                    var clip = graphics.Clip;
                    graphics.SetClip(new RectangleF(0, Height - LogicalToDeviceUnits(3), Width, LogicalToDeviceUnits(3)));
                    using (var pen = new Pen(theme.ControlBorderBottom))
                        graphics.DrawPath(pen, path);
                    graphics.Clip = clip;
                }
            }
            if (Focused && ShowFocusCues)
            {
                using var focus = new Pen(theme.TextPrimary, LogicalToDeviceUnits(2)) { Alignment = PenAlignment.Inset };
                graphics.DrawPath(focus, path);
            }
        }

        var textSize = string.IsNullOrEmpty(Text) ? Size.Empty : TextRenderer.MeasureText(Text, Font, Size.Empty, TextFormatFlags.NoPadding);
        var glyphSize = string.IsNullOrEmpty(_glyph) ? 0 : LogicalToDeviceUnits(16);
        var gap = glyphSize > 0 && textSize.Width > 0 ? LogicalToDeviceUnits(8) : 0;
        var x = (Width - glyphSize - gap - textSize.Width) / 2;
        if (glyphSize > 0)
        {
            using var font = Typography.Icons(glyphSize);
            TextRenderer.DrawText(graphics, _glyph, font, new Rectangle(x, 0, glyphSize, Height), text,
                TextFormatFlags.NoPadding | TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
        }
        if (textSize.Width > 0)
        {
            var flags = TextFormatFlags.NoPadding | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine;
            if (!ShowKeyboardCues) flags |= TextFormatFlags.HidePrefix;
            TextRenderer.DrawText(graphics, Text, Font, new Rectangle(x + glyphSize + gap, 0, textSize.Width + 1, Height), text, flags);
        }
    }

    protected override void OnMouseEnter(EventArgs e) { _hover = true; Invalidate(); base.OnMouseEnter(e); }

    protected override void OnMouseLeave(EventArgs e) { _hover = false; _pressed = false; Invalidate(); base.OnMouseLeave(e); }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        if (e.Button == MouseButtons.Left) { _pressed = true; Invalidate(); }
        base.OnMouseDown(e);
    }

    protected override void OnMouseUp(MouseEventArgs e) { _pressed = false; Invalidate(); base.OnMouseUp(e); }

    protected override void OnEnabledChanged(EventArgs e) { _hover = _pressed = false; Invalidate(); base.OnEnabledChanged(e); }

    protected override void OnTextChanged(EventArgs e) { PerformLayout(); Invalidate(); base.OnTextChanged(e); }
}

/// <summary>Windows 11 text box: rounded field whose bottom edge turns into an accent line on focus.</summary>
public sealed class FluentTextBox : Control
{
    private readonly TextBox _box = new() { BorderStyle = BorderStyle.None };

    public FluentTextBox()
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        SetStyle(ControlStyles.Selectable, false);
        TabStop = false;
        Cursor = Cursors.IBeam;
        Font = Typography.Body;
        Controls.Add(_box);
        _box.GotFocus += (_, _) => ApplyTheme();
        _box.LostFocus += (_, _) => ApplyTheme();
        _box.TextChanged += (_, _) => OnTextChanged(EventArgs.Empty);
        _box.KeyDown += (_, e) => OnKeyDown(e);
        ApplyTheme();
    }

    public TextBox Inner => _box;

    [AllowNull]
    public override string Text
    {
        get => _box.Text;
        set => _box.Text = value;
    }

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public string PlaceholderText
    {
        get => _box.PlaceholderText;
        set => _box.PlaceholderText = value;
    }

    public void ApplyTheme()
    {
        var theme = Theme.Current;
        _box.BackColor = _box.Focused ? theme.InputFillFocused : theme.InputFill;
        _box.ForeColor = theme.TextPrimary;
        Invalidate();
    }

    // Logical size; form auto-scaling brings it to the current DPI. Control's default is empty,
    // which left an anchored field zero pixels tall.
    protected override Size DefaultSize => new(240, 32);

    protected override void OnFontChanged(EventArgs e)
    {
        _box.Font = Font;
        base.OnFontChanged(e);
        PerformLayout();
    }

    protected override void OnLayout(LayoutEventArgs levent)
    {
        base.OnLayout(levent);
        var padding = LogicalToDeviceUnits(10);
        _box.SetBounds(padding, (Height - _box.Height) / 2, Math.Max(0, Width - padding * 2), _box.Height);
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        _box.Focus();
        base.OnMouseDown(e);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var theme = Theme.Current;
        var graphics = e.Graphics;
        var focused = _box.Focused;
        graphics.Clear(Parent?.BackColor ?? theme.Card);
        graphics.SmoothingMode = SmoothingMode.AntiAlias;
        using var path = Drawing.RoundedRect(new RectangleF(0.5f, 0.5f, Width - 1f, Height - 1f), LogicalToDeviceUnits(4));
        using (var fill = new SolidBrush(focused ? theme.InputFillFocused : theme.InputFill))
            graphics.FillPath(fill, path);
        using (var pen = new Pen(theme.InputBorder))
            graphics.DrawPath(pen, path);

        var thickness = focused ? LogicalToDeviceUnits(2) : 1;
        var clip = graphics.Clip;
        graphics.SetClip(path);
        using (var bottom = new SolidBrush(focused ? theme.Accent : theme.InputBorderBottom))
            graphics.FillRectangle(bottom, 0, Height - thickness, Width, thickness);
        graphics.Clip = clip;
    }
}

/// <summary>Status circle (check, "!", "×") or an indeterminate progress ring while synchronizing.</summary>
public sealed class StatusBadge : Control
{
    private readonly System.Windows.Forms.Timer _spin = new() { Interval = 30 };
    private StatusTone _tone = StatusTone.Neutral;
    private float _phase;

    public StatusBadge()
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        SetStyle(ControlStyles.Selectable, false);
        AccessibleRole = AccessibleRole.Graphic;
        _spin.Tick += (_, _) => { _phase = (_phase + 0.05f) % 1f; Invalidate(); };
    }

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public StatusTone Tone
    {
        get => _tone;
        set
        {
            if (_tone == value) return;
            _tone = value;
            UpdateSpin();
            Invalidate();
        }
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var theme = Theme.Current;
        var graphics = e.Graphics;
        graphics.Clear(Parent?.BackColor ?? theme.Card);
        var size = Math.Min(Width, Height) - 1f;
        var bounds = new RectangleF((Width - size) / 2f, (Height - size) / 2f, size, size);
        if (_tone != StatusTone.Busy)
        {
            Drawing.DrawStatusBadge(graphics, bounds, _tone, theme);
            return;
        }

        // Windows 11 ProgressRing: an arc that grows and shrinks while it turns.
        graphics.SmoothingMode = SmoothingMode.AntiAlias;
        var stroke = LogicalToDeviceUnits(3);
        var ring = RectangleF.Inflate(bounds, -stroke / 2f - 1, -stroke / 2f - 1);
        var sweep = 40f + 200f * (0.5f - 0.5f * MathF.Cos(_phase * MathF.PI * 2f));
        var start = _phase * 720f - 90f;
        using var pen = new Pen(theme.Accent, stroke) { StartCap = LineCap.Round, EndCap = LineCap.Round };
        graphics.DrawArc(pen, ring, start, sweep);
    }

    protected override void OnVisibleChanged(EventArgs e) { base.OnVisibleChanged(e); UpdateSpin(); }

    protected override void OnHandleCreated(EventArgs e) { base.OnHandleCreated(e); UpdateSpin(); }

    protected override void OnHandleDestroyed(EventArgs e) { _spin.Stop(); base.OnHandleDestroyed(e); }

    protected override void Dispose(bool disposing)
    {
        if (disposing) _spin.Dispose();
        base.Dispose(disposing);
    }

    private void UpdateSpin() => _spin.Enabled = _tone == StatusTone.Busy && Visible && IsHandleCreated;
}
