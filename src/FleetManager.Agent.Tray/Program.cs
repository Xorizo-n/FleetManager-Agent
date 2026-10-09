using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using FleetManager.Agent.Core;
using FleetManager.Agent.Ui;

namespace FleetManager.Agent.Tray;

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        // One icon per session: the Run key, the installer and a manual start may all launch the tray.
        using var instance = new Mutex(true, @"Local\FleetManagerAgent.Tray", out var isFirstInstance);
        if (!isFirstInstance) return;
        ApplicationConfiguration.Initialize();
        Application.Run(new TrayApplicationContext());
    }
}

internal sealed class TrayApplicationContext : ApplicationContext
{
    private const string ControlProcessName = "FleetManager.Agent.Control";
    private static readonly TimeSpan StatusTimeout = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan HoverRefreshInterval = TimeSpan.FromSeconds(5);

    private readonly AgentPipeClient _pipe = new();
    private readonly NotifyIcon _notifyIcon;
    private readonly ContextMenuStrip _menu;
    private readonly FluentMenuRenderer _renderer = new();
    private readonly StatusMenuHeader _header = new();
    private readonly ToolStripMenuItem _syncItem;
    private readonly ToolStripMenuItem _openItem;
    private readonly ToolStripMenuItem _hideItem;
    private readonly System.Windows.Forms.Timer _timer;
    private readonly TrayIconSet _icons = new();
    private readonly SynchronizationContext _ui;
    private AgentStatus? _status;
    private StatusSummary _summary;
    private bool _refreshing;
    private bool _syncing;
    private DateTime _lastRefresh = DateTime.MinValue;

    public TrayApplicationContext()
    {
        _syncItem = new ToolStripMenuItem("Синхронизировать сейчас", null, async (_, _) => await SyncAsync());
        _openItem = new ToolStripMenuItem("Открыть панель управления", null, (_, _) => OpenControl());
        _hideItem = new ToolStripMenuItem("Скрыть значок", null, (_, _) => ExitThread())
        {
            ToolTipText = "Служба агента продолжит работу.\nЗначок вернётся при следующем входе в систему."
        };
        _menu = new ContextMenuStrip { Renderer = _renderer, ShowItemToolTips = true, ShowCheckMargin = false, Font = Typography.Body };
        _menu.Items.AddRange(new ToolStripItem[] { _header, new ToolStripSeparator(), _syncItem, _openItem, new ToolStripSeparator(), _hideItem });
        _menu.HandleCreated += (_, _) => _renderer.DrawBorder = !WindowChrome.RoundPopup(_menu.Handle, Theme.Current.MenuBorder);
        _menu.Opening += (_, _) => _ = RefreshStatusAsync();
        // Creating the menu installed the WinForms context; theme notifications come from SystemEvents.
        _ui = SynchronizationContext.Current ?? new WindowsFormsSynchronizationContext();
        ApplyMenuTheme();

        _summary = _header.Summary;
        _notifyIcon = new NotifyIcon
        {
            Icon = _icons.Get(_summary.Tone),
            Text = AgentStatusPresenter.Tooltip(_summary),
            ContextMenuStrip = _menu,
            Visible = true
        };
        _notifyIcon.MouseDoubleClick += (_, e) => { if (e.Button == MouseButtons.Left) OpenControl(); };
        // The tooltip is read on hover: make sure it is not a stale state.
        _notifyIcon.MouseMove += (_, _) =>
        {
            if (DateTime.UtcNow - _lastRefresh > HoverRefreshInterval) _ = RefreshStatusAsync();
        };

        _timer = new System.Windows.Forms.Timer { Interval = 15_000 };
        _timer.Tick += async (_, _) => await RefreshStatusAsync();
        _timer.Start();
        Theme.Changed += OnThemeChanged;
        _ = RefreshStatusAsync();
    }

    private async Task RefreshStatusAsync()
    {
        if (_refreshing) return;
        _refreshing = true;
        try
        {
            _status = await _pipe.TryGetStatusAsync(StatusTimeout);
            _lastRefresh = DateTime.UtcNow;
            if (!_syncing) ShowStatus(AgentStatusPresenter.Describe(_status, DateTimeOffset.Now));
        }
        finally
        {
            _refreshing = false;
        }
    }

    private void ShowStatus(StatusSummary summary)
    {
        _summary = summary;
        // The presenter keeps the text within NotifyIcon's 127-character limit; the old code put
        // the raw status JSON here, the setter threw and the tooltip always said "недоступна".
        _notifyIcon.Text = AgentStatusPresenter.Tooltip(summary);
        _notifyIcon.Icon = _icons.Get(summary.Tone);
        _header.Summary = summary;
        _syncItem.Enabled = !_syncing && _status is { IsSyncing: false };
    }

    private async Task SyncAsync()
    {
        if (_syncing) return;
        _syncing = true;
        ShowStatus(new StatusSummary(StatusTone.Busy, "Синхронизация…", _summary.Tone == StatusTone.Success ? _summary.Detail : null));
        StatusSummary result;
        try
        {
            var response = await _pipe.SyncAsync();
            _status = response.DataAs<AgentStatus>() ?? await _pipe.TryGetStatusAsync(StatusTimeout);
            result = response.Success
                ? AgentStatusPresenter.Describe(_status, DateTimeOffset.Now)
                : new StatusSummary(StatusTone.Error, "Синхронизация не выполнена", response.Error);
        }
        catch (Exception ex) when (ex is IOException or TimeoutException or UnauthorizedAccessException or OperationCanceledException or JsonException)
        {
            _status = null;
            result = AgentStatusPresenter.ServiceUnavailable;
        }
        finally
        {
            _syncing = false;
        }

        ShowStatus(result);
        if (result.Tone == StatusTone.Success)
            Notify("Синхронизация выполнена", result.Detail ?? "Данные отправлены на сервер.", ToolTipIcon.Info);
        else
            Notify(result.Title, result.Detail ?? "Подробности — в панели управления.", ToolTipIcon.Warning);
    }

    private void OpenControl()
    {
        // Already open: bring it forward instead of asking for elevation again.
        if (WindowChrome.ActivateExisting(ControlProcessName)) return;
        try
        {
            var control = Path.Combine(AppContext.BaseDirectory, ControlProcessName + ".exe");
            Process.Start(new ProcessStartInfo(control) { UseShellExecute = true, Verb = "runas" });
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            // The user declined the UAC prompt.
        }
        catch (Exception ex)
        {
            Notify("Не удалось открыть панель управления", ex.Message, ToolTipIcon.Error);
        }
    }

    private void Notify(string title, string text, ToolTipIcon icon) =>
        _notifyIcon.ShowBalloonTip(5000, title, string.IsNullOrWhiteSpace(text) ? " " : text, icon);

    private void ApplyMenuTheme()
    {
        var theme = Theme.Current;
        var size = _menu.LogicalToDeviceUnits(16);
        _menu.ImageScalingSize = new Size(size, size);
        _menu.BackColor = theme.MenuBackground;
        _menu.ForeColor = theme.TextPrimary;
        SetImage(_syncItem, Drawing.GlyphBitmap(Glyphs.Sync, theme.TextPrimary, size));
        // Windows marks commands that elevate with the UAC shield.
        SetImage(_openItem, ShieldImage(size) ?? Drawing.GlyphBitmap(Glyphs.Settings, theme.TextPrimary, size));
        SetImage(_hideItem, Drawing.GlyphBitmap(Glyphs.Hide, theme.TextPrimary, size));
        foreach (ToolStripItem item in _menu.Items)
        {
            if (item is ToolStripMenuItem menuItem)
                menuItem.Padding = new Padding(0, _menu.LogicalToDeviceUnits(5), 0, _menu.LogicalToDeviceUnits(5));
        }
        if (_menu.IsHandleCreated) _renderer.DrawBorder = !WindowChrome.RoundPopup(_menu.Handle, theme.MenuBorder);
        _menu.Invalidate();
    }

    private static Bitmap? ShieldImage(int size)
    {
        try
        {
            using var shield = SystemIcons.GetStockIcon(StockIconId.Shield, size);
            return shield.ToBitmap();
        }
        catch (Exception ex) when (ex is Win32Exception or ArgumentException or ExternalException)
        {
            return null;
        }
    }

    private static void SetImage(ToolStripMenuItem item, Image? image)
    {
        var previous = item.Image;
        item.Image = image;
        item.ImageScaling = ToolStripItemImageScaling.None;
        previous?.Dispose();
    }

    private void OnThemeChanged(object? sender, EventArgs e) => _ui.Post(_ => ApplyMenuTheme(), null);

    protected override void ExitThreadCore()
    {
        Theme.Changed -= OnThemeChanged;
        _timer.Stop();
        _notifyIcon.Visible = false;
        _notifyIcon.Dispose();
        _menu.Dispose();
        _icons.Dispose();
        base.ExitThreadCore();
    }
}
