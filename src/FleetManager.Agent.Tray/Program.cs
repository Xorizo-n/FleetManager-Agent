using System.Diagnostics;
using System.ComponentModel;
using FleetManager.Agent.Core;

namespace FleetManager.Agent.Tray;

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        ApplicationConfiguration.Initialize();
        Application.Run(new TrayApplicationContext());
    }
}

internal sealed class TrayApplicationContext : ApplicationContext
{
    private readonly NotifyIcon _notifyIcon;
    private readonly System.Windows.Forms.Timer _timer;
    private readonly AgentPipeClient _pipe = new();

    public TrayApplicationContext()
    {
        var menu = new ContextMenuStrip();
        menu.Items.Add("Открыть управление", null, (_, _) => OpenControl());
        menu.Items.Add("Синхронизировать сейчас", null, async (_, _) => await SyncAsync());
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Выход", null, (_, _) => ExitThread());
        _notifyIcon = new NotifyIcon
        {
            Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath) ?? SystemIcons.Application,
            Text = "FleetManager Agent: запуск...",
            Visible = true,
            ContextMenuStrip = menu
        };
        _notifyIcon.DoubleClick += (_, _) => OpenControl();
        _timer = new System.Windows.Forms.Timer { Interval = 30_000 };
        _timer.Tick += async (_, _) => await RefreshStatusAsync();
        _timer.Start();
        _ = RefreshStatusAsync();
    }

    private async Task RefreshStatusAsync()
    {
        try
        {
            var response = await _pipe.GetStatusAsync();
            _notifyIcon.Text = response.Success ? $"FleetManager Agent: {ExtractState(response.Data)}" : "FleetManager Agent: служба недоступна";
        }
        catch { _notifyIcon.Text = "FleetManager Agent: служба недоступна"; }
    }

    private async Task SyncAsync()
    {
        try { await _pipe.SyncAsync(); await RefreshStatusAsync(); }
        catch { _notifyIcon.ShowBalloonTip(3000, "FleetManager Agent", "Служба агента недоступна.", ToolTipIcon.Warning); }
    }

    private void OpenControl()
    {
        try
        {
            var control = Path.Combine(AppContext.BaseDirectory, "FleetManager.Agent.Control.exe");
            Process.Start(new ProcessStartInfo(control) { UseShellExecute = true, Verb = "runas" });
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 1223) { }
        catch (Exception ex) { _notifyIcon.ShowBalloonTip(4000, "FleetManager Agent", ex.Message, ToolTipIcon.Error); }
    }

    private static string ExtractState(object? data) => data?.ToString() ?? "неизвестно";

    protected override void ExitThreadCore()
    {
        _timer.Stop();
        _notifyIcon.Visible = false;
        _notifyIcon.Dispose();
        base.ExitThreadCore();
    }
}
