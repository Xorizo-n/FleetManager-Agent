using System.Diagnostics;
using System.Runtime.InteropServices;

namespace FleetManager.Agent.Ui;

internal static class NativeMethods
{
    internal const int DWMWA_USE_IMMERSIVE_DARK_MODE_BEFORE_20H1 = 19;
    internal const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;
    internal const int DWMWA_WINDOW_CORNER_PREFERENCE = 33;
    internal const int DWMWA_BORDER_COLOR = 34;
    internal const int DWMWA_CAPTION_COLOR = 35;
    internal const int DWMWA_TEXT_COLOR = 36;
    internal const int DWMWA_COLOR_DEFAULT = unchecked((int)0xFFFFFFFF);
    internal const int DWMWCP_ROUND = 2;
    internal const int SW_RESTORE = 9;

    [DllImport("dwmapi.dll")]
    internal static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    [DllImport("uxtheme.dll", CharSet = CharSet.Unicode)]
    internal static extern int SetWindowTheme(IntPtr hwnd, string? subAppName, string? subIdList);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool SetForegroundWindow(IntPtr hwnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool ShowWindow(IntPtr hwnd, int command);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool IsIconic(IntPtr hwnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool DestroyIcon(IntPtr handle);

    [DllImport("user32.dll")]
    internal static extern IntPtr SendMessage(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam);

    internal const int WM_VSCROLL = 0x0115;
    internal const int SB_BOTTOM = 7;

    internal static bool SetAttribute(IntPtr hwnd, int attribute, int value) =>
        DwmSetWindowAttribute(hwnd, attribute, ref value, sizeof(int)) == 0;
}

/// <summary>Window-level Windows 11 effects applied through DWM.</summary>
public static class WindowChrome
{
    /// <summary>
    /// Dark title bar in dark mode and a caption coloured like the window background,
    /// so the title bar and the content read as one surface (Windows 11 22000+; ignored before).
    /// </summary>
    public static void Apply(Form form, Theme theme)
    {
        if (!form.IsHandleCreated) return;
        var handle = form.Handle;
        var dark = theme.IsDark ? 1 : 0;
        if (!NativeMethods.SetAttribute(handle, NativeMethods.DWMWA_USE_IMMERSIVE_DARK_MODE, dark))
            NativeMethods.SetAttribute(handle, NativeMethods.DWMWA_USE_IMMERSIVE_DARK_MODE_BEFORE_20H1, dark);
        NativeMethods.SetAttribute(handle, NativeMethods.DWMWA_CAPTION_COLOR,
            theme.IsHighContrast ? NativeMethods.DWMWA_COLOR_DEFAULT : ColorTranslator.ToWin32(theme.Background));
        NativeMethods.SetAttribute(handle, NativeMethods.DWMWA_TEXT_COLOR,
            theme.IsHighContrast ? NativeMethods.DWMWA_COLOR_DEFAULT : ColorTranslator.ToWin32(theme.TextPrimary));
    }

    /// <summary>Rounds a popup (menu) the way Windows 11 rounds its own; false on Windows 10.</summary>
    public static bool RoundPopup(IntPtr handle, Color border)
    {
        if (!NativeMethods.SetAttribute(handle, NativeMethods.DWMWA_WINDOW_CORNER_PREFERENCE, NativeMethods.DWMWCP_ROUND))
            return false;
        NativeMethods.SetAttribute(handle, NativeMethods.DWMWA_BORDER_COLOR, ColorTranslator.ToWin32(border));
        return true;
    }

    /// <summary>
    /// Scrolls to the last line. RichTextBox.ScrollToCaret stops short with word wrap on,
    /// leaving the newest log entries below the visible area.
    /// </summary>
    public static void ScrollToBottom(Control control)
    {
        if (control.IsHandleCreated)
            NativeMethods.SendMessage(control.Handle, NativeMethods.WM_VSCROLL, NativeMethods.SB_BOTTOM, IntPtr.Zero);
    }

    /// <summary>Dark or light scroll bars for a scrollable control.</summary>
    public static void ApplyScrollBars(Control control, Theme theme)
    {
        if (control.IsHandleCreated)
            NativeMethods.SetWindowTheme(control.Handle, theme.IsDark && !theme.IsHighContrast ? "DarkMode_Explorer" : "Explorer", null);
    }

    /// <summary>
    /// Brings the main window of another running instance to the front.
    /// Works for an elevated Control from the unelevated tray too: no UAC prompt for a window already open.
    /// </summary>
    public static bool ActivateExisting(string processName)
    {
        try
        {
            foreach (var process in Process.GetProcessesByName(processName))
            {
                using (process)
                {
                    if (process.Id == Environment.ProcessId) continue;
                    var handle = process.MainWindowHandle;
                    if (handle == IntPtr.Zero) continue;
                    if (NativeMethods.IsIconic(handle)) NativeMethods.ShowWindow(handle, NativeMethods.SW_RESTORE);
                    NativeMethods.SetForegroundWindow(handle);
                    return true;
                }
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
        }
        return false;
    }
}
