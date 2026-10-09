using FleetManager.Agent.Core;
using Microsoft.Win32;

namespace FleetManager.Agent.Ui;

/// <summary>
/// Windows 11 colour tokens (Mica base, cards, controls) for the current system theme.
/// Follows the light/dark app mode, the accent colour and high contrast.
/// </summary>
public sealed class Theme
{
    private const string PersonalizeKey = @"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize";
    private const string AccentKey = @"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Explorer\Accent";

    private static Theme? _current;

    public static Theme Current => _current ??= Detect();

    /// <summary>Raised on the UI thread when the system theme, accent or contrast changes.</summary>
    public static event EventHandler? Changed;

    static Theme()
    {
        SystemEvents.UserPreferenceChanged += (_, e) =>
        {
            if (e.Category is not (UserPreferenceCategory.General or UserPreferenceCategory.Color
                or UserPreferenceCategory.VisualStyle or UserPreferenceCategory.Accessibility)) return;
            var detected = Detect();
            if (_current is not null && detected.Signature == _current.Signature) return;
            _current = detected;
            Changed?.Invoke(null, EventArgs.Empty);
        };
    }

    public bool IsDark { get; private init; }
    public bool IsHighContrast { get; private init; }

    public Color Background { get; private init; }
    public Color Card { get; private init; }
    public Color CardBorder { get; private init; }
    public Color Divider { get; private init; }

    public Color TextPrimary { get; private init; }
    public Color TextSecondary { get; private init; }
    public Color TextTertiary { get; private init; }
    public Color TextDisabled { get; private init; }

    public Color Accent { get; private init; }
    public Color AccentHover { get; private init; }
    public Color AccentPressed { get; private init; }
    public Color AccentDisabled { get; private init; }
    public Color OnAccent { get; private init; }
    /// <summary>Accent for text and glyphs on cards (AccentTextFillColorPrimary).</summary>
    public Color AccentText { get; private init; }

    public Color ControlFill { get; private init; }
    public Color ControlFillHover { get; private init; }
    public Color ControlFillPressed { get; private init; }
    public Color ControlFillDisabled { get; private init; }
    public Color ControlBorder { get; private init; }
    /// <summary>Slightly darker bottom edge that gives buttons their elevation.</summary>
    public Color ControlBorderBottom { get; private init; }

    public Color InputFill { get; private init; }
    public Color InputFillFocused { get; private init; }
    public Color InputBorder { get; private init; }
    public Color InputBorderBottom { get; private init; }

    public Color MenuBackground { get; private init; }
    public Color MenuBorder { get; private init; }
    public Color MenuHover { get; private init; }

    public Color Success { get; private init; }
    public Color Caution { get; private init; }
    public Color Critical { get; private init; }
    public Color Neutral { get; private init; }

    private string Signature => $"{IsDark}|{IsHighContrast}|{Accent.ToArgb()}|{Background.ToArgb()}";

    public Color ToneColor(StatusTone tone) => tone switch
    {
        StatusTone.Success => Success,
        StatusTone.Warning => Caution,
        StatusTone.Error => Critical,
        StatusTone.Busy => AccentText,
        _ => Neutral
    };

    /// <summary>Glyph colour on a filled <see cref="ToneColor"/> circle.</summary>
    public Color OnTone => IsHighContrast ? SystemColors.Window : IsDark ? Color.Black : Color.White;

    /// <summary>
    /// Tray overlay dot. The taskbar is themed separately from apps and is translucent,
    /// so these are the vivid shell colours rather than the app's text-safe ones.
    /// </summary>
    public static Color? TrayDot(StatusTone tone) => tone switch
    {
        StatusTone.Warning => Color.FromArgb(0xFF, 0xB9, 0x00),
        StatusTone.Error => Color.FromArgb(0xE8, 0x11, 0x23),
        _ => null
    };

    public static Theme Detect()
    {
        if (SystemInformation.HighContrast) return HighContrast();
        var dark = Registry.GetValue(PersonalizeKey, "AppsUseLightTheme", 1) is int light && light == 0;
        var palette = ReadAccentPalette();
        return dark ? Dark(palette) : Light(palette);
    }

    private static Theme Light(Color[]? palette)
    {
        var card = Hex(0xFBFBFB);
        // Primary buttons use AccentDark1 in light mode; text accent is AccentDark2.
        var accent = palette?[4] ?? Hex(0x0067C0);
        return new Theme
        {
            Background = Hex(0xF3F3F3),
            Card = card,
            CardBorder = Hex(0xE5E5E5),
            Divider = Hex(0xEAEAEA),
            TextPrimary = Hex(0x1A1A1A),
            TextSecondary = Hex(0x5D5D5D),
            TextTertiary = Hex(0x8A8A8A),
            TextDisabled = Hex(0xA0A0A0),
            Accent = accent,
            AccentHover = Drawing.Blend(accent, card, 0.9),
            AccentPressed = Drawing.Blend(accent, card, 0.8),
            AccentDisabled = Hex(0xBFBFBF),
            OnAccent = Color.White,
            AccentText = palette?[5] ?? Hex(0x003E92),
            ControlFill = Hex(0xFDFDFD),
            ControlFillHover = Hex(0xF6F6F6),
            ControlFillPressed = Hex(0xF1F1F1),
            ControlFillDisabled = Hex(0xF5F5F5),
            ControlBorder = Hex(0xE3E3E3),
            ControlBorderBottom = Hex(0xCCCCCC),
            InputFill = Hex(0xFFFFFF),
            InputFillFocused = Hex(0xFFFFFF),
            InputBorder = Hex(0xE3E3E3),
            InputBorderBottom = Hex(0x8A8A8A),
            MenuBackground = Hex(0xF9F9F9),
            MenuBorder = Hex(0xDADADA),
            MenuHover = Hex(0xEBEBEB),
            Success = Hex(0x0F7B0F),
            Caution = Hex(0x9D5D00),
            Critical = Hex(0xC42B1C),
            Neutral = Hex(0x8A8A8A)
        };
    }

    private static Theme Dark(Color[]? palette)
    {
        var card = Hex(0x2B2B2B);
        // Primary buttons use AccentLight2 in dark mode; text accent is AccentLight3.
        var accent = palette?[1] ?? Hex(0x4CC2FF);
        return new Theme
        {
            IsDark = true,
            Background = Hex(0x202020),
            Card = card,
            CardBorder = Hex(0x1C1C1C),
            Divider = Hex(0x3A3A3A),
            TextPrimary = Hex(0xFFFFFF),
            TextSecondary = Hex(0xC9C9C9),
            TextTertiary = Hex(0x9E9E9E),
            TextDisabled = Hex(0x717171),
            Accent = accent,
            AccentHover = Drawing.Blend(accent, card, 0.9),
            AccentPressed = Drawing.Blend(accent, card, 0.8),
            AccentDisabled = Hex(0x434343),
            OnAccent = Color.Black,
            AccentText = palette?[0] ?? Hex(0x99EBFF),
            ControlFill = Hex(0x373737),
            ControlFillHover = Hex(0x3D3D3D),
            ControlFillPressed = Hex(0x323232),
            ControlFillDisabled = Hex(0x313131),
            ControlBorder = Hex(0x444444),
            ControlBorderBottom = Hex(0x3A3A3A),
            InputFill = Hex(0x343434),
            InputFillFocused = Hex(0x1F1F1F),
            InputBorder = Hex(0x444444),
            InputBorderBottom = Hex(0x9A9A9A),
            MenuBackground = Hex(0x2C2C2C),
            MenuBorder = Hex(0x404040),
            MenuHover = Hex(0x383838),
            Success = Hex(0x6CCB5F),
            Caution = Hex(0xFCE100),
            Critical = Hex(0xFF99A4),
            Neutral = Hex(0x9D9D9D)
        };
    }

    private static Theme HighContrast() => new()
    {
        IsHighContrast = true,
        IsDark = SystemColors.Window.GetBrightness() < 0.5f,
        Background = SystemColors.Window,
        Card = SystemColors.Window,
        CardBorder = SystemColors.WindowText,
        Divider = SystemColors.WindowText,
        TextPrimary = SystemColors.WindowText,
        TextSecondary = SystemColors.WindowText,
        TextTertiary = SystemColors.WindowText,
        TextDisabled = SystemColors.GrayText,
        Accent = SystemColors.Highlight,
        AccentHover = SystemColors.Highlight,
        AccentPressed = SystemColors.Highlight,
        AccentDisabled = SystemColors.GrayText,
        OnAccent = SystemColors.HighlightText,
        AccentText = SystemColors.HotTrack,
        ControlFill = SystemColors.ButtonFace,
        ControlFillHover = SystemColors.ButtonFace,
        ControlFillPressed = SystemColors.ButtonFace,
        ControlFillDisabled = SystemColors.ButtonFace,
        ControlBorder = SystemColors.ButtonShadow,
        ControlBorderBottom = SystemColors.ButtonShadow,
        InputFill = SystemColors.Window,
        InputFillFocused = SystemColors.Window,
        InputBorder = SystemColors.WindowText,
        InputBorderBottom = SystemColors.WindowText,
        MenuBackground = SystemColors.Menu,
        MenuBorder = SystemColors.MenuText,
        MenuHover = SystemColors.MenuHighlight,
        Success = SystemColors.WindowText,
        Caution = SystemColors.WindowText,
        Critical = SystemColors.WindowText,
        Neutral = SystemColors.WindowText
    };

    /// <summary>AccentPalette: eight RGBA colours, Light3, Light2, Light1, Base, Dark1, Dark2, Dark3, unused.</summary>
    private static Color[]? ReadAccentPalette()
    {
        try
        {
            if (Registry.GetValue(AccentKey, "AccentPalette", null) is not byte[] { Length: >= 32 } data) return null;
            return Enumerable.Range(0, 8).Select(i => Color.FromArgb(data[i * 4], data[i * 4 + 1], data[i * 4 + 2])).ToArray();
        }
        catch
        {
            return null;
        }
    }

    private static Color Hex(int rgb) => Color.FromArgb(255, (rgb >> 16) & 0xFF, (rgb >> 8) & 0xFF, rgb & 0xFF);
}
