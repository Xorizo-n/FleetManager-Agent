using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using FleetManager.Agent.Core;
using FleetManager.Agent.Ui;
// The namespace FleetManager.Agent.Control hides the WinForms type.
using WinControl = System.Windows.Forms.Control;

namespace FleetManager.Agent.Control;

internal static class Program
{
    internal const string ProcessName = "FleetManager.Agent.Control";

    [STAThread]
    private static void Main()
    {
        // A second launch (tray menu, shortcut) brings the open window forward instead.
        using var instance = new Mutex(true, @"Local\FleetManagerAgent.Control", out var isFirstInstance);
        if (!isFirstInstance)
        {
            WindowChrome.ActivateExisting(ProcessName);
            return;
        }
        ApplicationConfiguration.Initialize();
        Application.Run(new ControlForm());
    }
}

internal sealed partial class ControlForm : Form
{
    private static readonly TimeSpan PipeTimeout = TimeSpan.FromSeconds(5);
    private const int LogLines = 200;

    private readonly AgentPipeClient _pipe = new();
    private readonly System.Windows.Forms.Timer _refreshTimer = new() { Interval = 5000 };
    private readonly ToolTip _toolTip = new();
    private readonly List<CardPanel> _cards = new();
    private readonly List<Label> _secondary = new();
    private readonly List<Label> _tertiary = new();
    private readonly List<Panel> _dividers = new();
    private readonly ContextMenuStrip _copyMenu = new() { Font = Typography.Body };
    private readonly FluentMenuRenderer _copyMenuRenderer = new();

    private readonly StatusBadge _badge = new() { Size = new Size(40, 40), Margin = new Padding(0, 0, 16, 0), Anchor = AnchorStyles.None };
    private readonly Label _statusTitle;
    private readonly Label _statusDetail;
    private readonly Label _statusExtra;
    private readonly FluentButton _primaryButton = new() { Text = "Синхронизировать", Glyph = Glyphs.Sync, IsAccent = true, Anchor = AnchorStyles.Right, Margin = new Padding(16, 0, 0, 0) };

    private readonly FluentTextBox _serverUrl = new() { Anchor = AnchorStyles.Left | AnchorStyles.Right, Margin = new Padding(16, 0, 8, 0), PlaceholderText = "http://fleet.example.com" };
    private readonly FluentButton _saveButton = new() { Text = "Сохранить", Anchor = AnchorStyles.None, Margin = Padding.Empty, Enabled = false };
    private readonly Label _serverMessage;

    private readonly Label _computerName;
    private readonly Label _agentVersion;
    private readonly Label _softwareCount;
    private readonly Label _startedAt;
    private readonly Label _machineId;

    private readonly RichTextBox _logs = new()
    {
        Dock = DockStyle.Fill,
        BorderStyle = BorderStyle.None,
        ReadOnly = true,
        DetectUrls = false,
        WordWrap = true,
        ScrollBars = RichTextBoxScrollBars.Vertical,
        Font = Typography.Monospace,
        Margin = Padding.Empty
    };
    private readonly FluentButton _refreshLogsButton = new() { Text = "Обновить", Glyph = Glyphs.Refresh, Margin = new Padding(8, 0, 0, 0), Anchor = AnchorStyles.Right };
    private readonly FluentButton _openFolderButton = new() { Text = "Открыть папку", Glyph = Glyphs.FolderOpen, Margin = new Padding(8, 0, 0, 0), Anchor = AnchorStyles.Right };

    private AgentStatus? _status;
    private string _serverUrlFromService = string.Empty;
    private IReadOnlyList<string>? _logLines;
    private string? _logMessage;
    private bool _syncing;
    private bool _startingService;
    private bool _refreshing;
    // Until the first answer (or timeout) the panel cannot tell a stopped service from a slow one.
    private bool _statusKnown;
    private string? _serviceError;

    public ControlForm()
    {
        SuspendLayout();
        AutoScaleDimensions = new SizeF(96F, 96F);
        AutoScaleMode = AutoScaleMode.Dpi;
        Text = AgentStatusPresenter.ProductTitle;
        Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath);
        Font = Typography.Body;
        // Fits a 1920x1080 screen at 150% (1280x720 logical) with the taskbar.
        ClientSize = new Size(820, 640);
        MinimumSize = new Size(720, 640);
        StartPosition = FormStartPosition.CenterScreen;
        Padding = new Padding(20, 16, 20, 20);
        KeyPreview = true;

        _statusTitle = SingleLine(NewLabel(Typography.Subtitle));
        _statusDetail = SingleLine(NewLabel(Typography.Body, _secondary));
        _statusExtra = SingleLine(NewLabel(Typography.Caption, _tertiary));
        _statusExtra.Margin = new Padding(0, 4, 0, 0);
        _serverMessage = SingleLine(NewLabel(Typography.Caption));
        _serverMessage.Margin = new Padding(16, 6, 0, 0);
        _serverMessage.Visible = false;
        _computerName = NewValue();
        _agentVersion = NewValue();
        _softwareCount = NewValue();
        _startedAt = NewValue();
        _machineId = NewValue();
        // Values are often copied for support requests: right click copies.
        _copyMenu.Renderer = _copyMenuRenderer;
        _copyMenu.Items.Add("Копировать", null, (_, _) =>
        {
            if (_copyMenu.SourceControl is Label { Text: { Length: > 0 } text } && text != "—") Clipboard.SetText(text);
        });
        _copyMenu.HandleCreated += (_, _) => _copyMenuRenderer.DrawBorder = !WindowChrome.RoundPopup(_copyMenu.Handle, Theme.Current.MenuBorder);
        _badge.AccessibleName = "Состояние агента";
        _serverUrl.Inner.AccessibleName = "Адрес сервера";
        _logs.AccessibleName = "Журнал агента";
        // The status card keeps its three lines (empty ones included), but the window can still
        // be resized: keep the newest entries in view unless the user is reading the log.
        _logs.Resize += (_, _) =>
        {
            if (!_logs.Focused) WindowChrome.ScrollToBottom(_logs);
        };

        var root = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 4, Margin = Padding.Empty, Padding = Padding.Empty };
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        var status = Card(BuildStatus());
        status.Padding = new Padding(20, 18, 20, 18);
        root.Controls.Add(status, 0, 0);
        root.Controls.Add(Card(BuildServer()), 0, 1);
        root.Controls.Add(Card(BuildDetails()), 0, 2);
        var journal = Card(BuildJournal());
        journal.AutoSize = false;
        journal.Margin = Padding.Empty;
        root.Controls.Add(journal, 0, 3);
        Controls.Add(root);

        _primaryButton.Click += async (_, _) =>
        {
            if (_status is null) await StartServiceAsync();
            else await SyncAsync();
        };
        _saveButton.Click += async (_, _) => await SaveServerUrlAsync();
        _serverUrl.TextChanged += (_, _) => OnServerUrlEdited();
        _serverUrl.KeyDown += async (_, e) =>
        {
            if (e.KeyCode != Keys.Enter) return;
            e.SuppressKeyPress = true;
            if (_saveButton.Enabled) await SaveServerUrlAsync();
        };
        _refreshLogsButton.Click += async (_, _) => await RefreshLogsAsync();
        _openFolderButton.Click += (_, _) => OpenLogFolder();
        _refreshTimer.Tick += async (_, _) => await RefreshStatusAsync();
        Theme.Changed += OnThemeChanged;
        ApplyTheme();
        ShowStatus();
        ResumeLayout(false);
        PerformLayout();
    }

    protected override async void OnShown(EventArgs e)
    {
        base.OnShown(e);
        // WinForms focuses the first enabled control — the address field, caret and all — while
        // the primary button waits for the first status. UpdatePrimaryButton focuses it later.
        ActiveControl = null;
        _refreshTimer.Start();
        await Task.WhenAll(RefreshStatusAsync(), RefreshLogsAsync());
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        WindowChrome.Apply(this, Theme.Current);
    }

    protected override async void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.KeyCode != Keys.F5) return;
        e.Handled = true;
        await Task.WhenAll(RefreshStatusAsync(), RefreshLogsAsync());
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        Theme.Changed -= OnThemeChanged;
        _refreshTimer.Stop();
        base.OnFormClosed(e);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _refreshTimer.Dispose();
            _toolTip.Dispose();
            _copyMenu.Dispose();
        }
        base.Dispose(disposing);
    }

    // ---- Layout ---------------------------------------------------------------------------

    // Each card is a single TableLayoutPanel row with its text stacked in a nested panel:
    // row spans in an auto-sized TableLayoutPanel distribute height unpredictably.
    private WinControl BuildStatus()
    {
        var layout = Grid(3);
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        _statusTitle.Text = "Проверка состояния…";
        _statusDetail.Margin = new Padding(0, 2, 0, 0);
        layout.Controls.Add(_badge, 0, 0);
        layout.Controls.Add(Stack(_statusTitle, _statusDetail, _statusExtra), 1, 0);
        layout.Controls.Add(_primaryButton, 2, 0);
        return layout;
    }

    private WinControl BuildServer()
    {
        var layout = Grid(4);
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 40));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 60));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        var title = SingleLine(NewLabel(Typography.BodyStrong, text: "Адрес сервера"));
        var hint = SingleLine(NewLabel(Typography.Caption, _secondary, "Куда агент отправляет данные"));
        hint.Margin = new Padding(0, 2, 0, 0);
        layout.Controls.Add(NewIcon(Glyphs.Globe), 0, 0);
        layout.Controls.Add(Stack(title, hint), 1, 0);
        layout.Controls.Add(_serverUrl, 2, 0);
        layout.Controls.Add(_saveButton, 3, 0);
        // Validation and save results go under the field, like a Windows 11 text box.
        layout.Controls.Add(_serverMessage, 2, 1);
        layout.SetColumnSpan(_serverMessage, 2);
        return layout;
    }

    private WinControl BuildDetails()
    {
        var layout = Grid(5);
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        var icon = NewIcon(Glyphs.Computer);
        layout.Controls.Add(icon, 0, 0);
        var title = SingleLine(NewLabel(Typography.BodyStrong, text: "Этот компьютер"));
        title.Margin = new Padding(0, 0, 0, 8);
        layout.Controls.Add(title, 1, 0);
        layout.SetColumnSpan(title, 4);
        AddPair(layout, 1, 1, "Имя компьютера", _computerName);
        AddPair(layout, 3, 1, "Версия агента", _agentVersion);
        AddPair(layout, 1, 2, "Программ найдено", _softwareCount);
        AddPair(layout, 3, 2, "Служба запущена", _startedAt);
        AddPair(layout, 1, 3, "Идентификатор", _machineId);
        layout.SetColumnSpan(_machineId, 3);
        return layout;
    }

    private WinControl BuildJournal()
    {
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 3, Margin = Padding.Empty, Padding = Padding.Empty };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 1));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

        var header = Grid(4);
        header.Dock = DockStyle.Fill;
        header.Margin = new Padding(0, 0, 0, 12);
        header.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        header.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        header.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        header.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        var hint = SingleLine(NewLabel(Typography.Caption, _secondary, $"Последние {LogLines} записей службы · F5 — обновить"));
        hint.Margin = new Padding(0, 2, 0, 0);
        header.Controls.Add(NewIcon(Glyphs.Document), 0, 0);
        header.Controls.Add(Stack(SingleLine(NewLabel(Typography.BodyStrong, text: "Журнал")), hint), 1, 0);
        header.Controls.Add(_refreshLogsButton, 2, 0);
        header.Controls.Add(_openFolderButton, 3, 0);

        var divider = new Panel { Dock = DockStyle.Fill, Margin = Padding.Empty };
        _dividers.Add(divider);
        _logs.Margin = new Padding(0, 12, 0, 0);
        layout.Controls.Add(header, 0, 0);
        layout.Controls.Add(divider, 0, 1);
        layout.Controls.Add(_logs, 0, 2);
        return layout;
    }

    private CardPanel Card(WinControl content)
    {
        var card = new CardPanel
        {
            Dock = DockStyle.Fill,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Margin = new Padding(0, 0, 0, 8)
        };
        content.Dock = DockStyle.Fill;
        card.Controls.Add(content);
        _cards.Add(card);
        return card;
    }

    private static TableLayoutPanel Stack(params WinControl[] rows)
    {
        var stack = Grid(1);
        stack.RowCount = rows.Length;
        // Left|Right without Top|Bottom: stretched across the cell, centred vertically.
        stack.Anchor = AnchorStyles.Left | AnchorStyles.Right;
        stack.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        foreach (var row in rows)
        {
            stack.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            stack.Controls.Add(row);
        }
        return stack;
    }

    private static TableLayoutPanel Grid(int columns) => new()
    {
        AutoSize = true,
        AutoSizeMode = AutoSizeMode.GrowAndShrink,
        ColumnCount = columns,
        Margin = Padding.Empty,
        Padding = Padding.Empty
    };

    private void AddPair(TableLayoutPanel layout, int column, int row, string caption, Label value)
    {
        var label = NewLabel(Typography.Body, _secondary, caption);
        label.Margin = new Padding(0, 3, 16, 3);
        value.Margin = new Padding(0, 3, 24, 3);
        layout.Controls.Add(label, column, row);
        layout.Controls.Add(value, column + 1, row);
    }

    /// <summary>
    /// One line cut with "…" instead of wrapping when the window is narrow; a truncated label
    /// shows its full text as a tooltip. Without this an auto-sized label in a percent column
    /// wraps, and the extra line is clipped by the auto-sized row.
    /// </summary>
    private static Label SingleLine(Label label)
    {
        label.AutoSize = false;
        label.AutoEllipsis = true;
        label.Dock = DockStyle.Fill;
        // Logical pixels at 96 DPI; form auto-scaling brings it to the current DPI.
        label.Height = (int)Math.Ceiling(label.Font.GetHeight(96f)) + 3;
        return label;
    }

    private static Label NewLabel(Font font, List<Label>? group = null, string text = "")
    {
        var label = new Label { AutoSize = true, Font = font, Text = text, UseMnemonic = false, Margin = Padding.Empty };
        group?.Add(label);
        return label;
    }

    private Label NewValue()
    {
        var label = SingleLine(NewLabel(Typography.Body, text: "—"));
        label.ContextMenuStrip = _copyMenu;
        return label;
    }

    private Label NewIcon(string glyph) => new()
    {
        AutoSize = true,
        Text = glyph,
        Font = Typography.Icons(LogicalToDeviceUnits(20)),
        Margin = new Padding(0, 0, 16, 0),
        Anchor = AnchorStyles.Left,
        UseMnemonic = false,
        AccessibleRole = AccessibleRole.Graphic
    };

    // ---- Theme ----------------------------------------------------------------------------

    private void OnThemeChanged(object? sender, EventArgs e)
    {
        if (IsDisposed) return;
        if (InvokeRequired) BeginInvoke(ApplyTheme);
        else ApplyTheme();
    }

    private void ApplyTheme()
    {
        var theme = Theme.Current;
        BackColor = theme.Background;
        ForeColor = theme.TextPrimary;
        foreach (var card in _cards)
        {
            card.BackColor = theme.Card;
            card.BorderColor = theme.CardBorder;
        }
        foreach (var label in _secondary) label.ForeColor = theme.TextSecondary;
        foreach (var label in _tertiary) label.ForeColor = theme.TextTertiary;
        foreach (var divider in _dividers) divider.BackColor = theme.Divider;
        _serverUrl.ApplyTheme();
        _logs.BackColor = theme.Card;
        _logs.ForeColor = theme.TextPrimary;
        RenderLogs();
        ShowServerMessage(_serverMessage.Text, _serverMessage.Tag as StatusTone?);
        WindowChrome.Apply(this, theme);
        if (_logs.IsHandleCreated) WindowChrome.ApplyScrollBars(_logs, theme);
        else _logs.HandleCreated += (_, _) => WindowChrome.ApplyScrollBars(_logs, Theme.Current);
        Invalidate(true);
    }

    // ---- Status ---------------------------------------------------------------------------

    private async Task RefreshStatusAsync()
    {
        if (_refreshing) return;
        _refreshing = true;
        try
        {
            _status = await _pipe.TryGetStatusAsync(PipeTimeout);
            _statusKnown = true;
            if (_status is not null) _serviceError = null;
            ShowStatus();
        }
        finally
        {
            _refreshing = false;
        }
    }

    private void ShowStatus()
    {
        var now = DateTimeOffset.Now;
        var summary = !_statusKnown
            ? new StatusSummary(StatusTone.Busy, "Проверка состояния…")
            : _startingService
                ? new StatusSummary(StatusTone.Busy, "Запуск службы…")
                : AgentStatusPresenter.Describe(_status, now);
        if (_syncing && summary.Tone != StatusTone.Busy)
            summary = new StatusSummary(StatusTone.Busy, "Синхронизация…", summary.Tone == StatusTone.Success ? summary.Detail : null);
        if (_status is null && _serviceError is not null && !_startingService)
            summary = summary with { Error = _serviceError };

        _badge.Tone = summary.Tone;
        _badge.AccessibleDescription = summary.Title;
        _statusTitle.Text = summary.Title;
        _statusDetail.Text = summary.Detail ?? string.Empty;
        var extra = summary.Error is { Length: > 0 } error
            ? FirstLine(error)
            : AgentStatusPresenter.NextSync(_status, now);
        _statusExtra.Text = extra ?? string.Empty;
        _toolTip.SetToolTip(_statusExtra, summary.Error);
        UpdatePrimaryButton();

        _computerName.Text = _status?.ComputerName ?? Environment.MachineName;
        _agentVersion.Text = _status?.AgentVersion ?? AgentVersion.Current ?? "—";
        _softwareCount.Text = _status is { LastSyncAt: not null } ? _status.SoftwareCount.ToString("N0", CultureInfo.GetCultureInfo("ru-RU")) : "—";
        _startedAt.Text = _status is null ? "—" : AgentStatusPresenter.FormatMoment(_status.StartedAt, now);
        _machineId.Text = _status?.MachineId ?? "—";

        if (_status is not null && !IsServerUrlEdited())
        {
            _serverUrlFromService = _status.ServerUrl;
            if (_serverUrl.Text != _serverUrlFromService) _serverUrl.Text = _serverUrlFromService;
        }
        UpdateSaveButton();
    }

    /// <summary>"Синхронизировать", or "Запустить службу" while the service does not answer.</summary>
    private void UpdatePrimaryButton()
    {
        var startService = _statusKnown && _status is null;
        _primaryButton.Text = startService ? "Запустить службу" : "Синхронизировать";
        _primaryButton.Glyph = startService ? Glyphs.Play : Glyphs.Sync;
        var enabled = _statusKnown && !_syncing && !_startingService && (startService || _status is { IsSyncing: false });
        // Disabling the focused button would push focus into the address field, caret and all.
        if (!enabled && _primaryButton.Focused) ActiveControl = null;
        _primaryButton.Enabled = enabled;
        if (enabled && ActiveControl is null && ContainsFocus) ActiveControl = _primaryButton;
    }

    /// <summary>The panel runs elevated, so a stopped service can be started right here.</summary>
    private async Task StartServiceAsync()
    {
        if (_startingService) return;
        _startingService = true;
        _serviceError = null;
        ShowStatus();
        try
        {
            var exitCode = await RunServiceControlAsync("start", AgentConfiguration.ServiceName);
            // 1056: already running — then it only needs a moment to open its pipe.
            if (exitCode is 0 or 1056)
            {
                for (var attempt = 0; attempt < 20 && _status is null; attempt++)
                {
                    await Task.Delay(1000);
                    _status = await _pipe.TryGetStatusAsync(PipeTimeout);
                }
            }
            _serviceError = exitCode switch
            {
                0 or 1056 => _status is null ? "Служба запущена, но пока не отвечает." : null,
                1058 => "Служба отключена: включите её в оснастке «Службы» (services.msc).",
                1060 => $"Служба {AgentConfiguration.ServiceName} не установлена — переустановите агент.",
                _ => $"Не удалось запустить службу (код {exitCode})."
            };
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            _serviceError = $"Не удалось запустить службу: {ex.Message}";
        }
        finally
        {
            _startingService = false;
        }
        ShowStatus();
        await RefreshLogsAsync();
    }

    private static async Task<int> RunServiceControlAsync(params string[] arguments)
    {
        var info = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "sc.exe"))
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true
        };
        foreach (var argument in arguments) info.ArgumentList.Add(argument);
        using var process = Process.Start(info) ?? throw new InvalidOperationException("sc.exe did not start.");
        await process.StandardOutput.ReadToEndAsync();
        await process.WaitForExitAsync();
        // sc.exe exits with the Win32 error code of the request.
        return process.ExitCode;
    }

    private async Task SyncAsync()
    {
        if (_syncing) return;
        _syncing = true;
        ShowStatus();
        try
        {
            var response = await _pipe.SyncAsync();
            _status = response.DataAs<AgentStatus>() ?? await _pipe.TryGetStatusAsync(PipeTimeout);
        }
        catch (Exception ex) when (ex is IOException or TimeoutException or UnauthorizedAccessException or OperationCanceledException or JsonException)
        {
            _status = null;
        }
        finally
        {
            _syncing = false;
        }
        ShowStatus();
        await RefreshLogsAsync();
    }

    // ---- Server URL -----------------------------------------------------------------------

    private bool IsServerUrlEdited() => !string.Equals(_serverUrl.Text.Trim(), _serverUrlFromService, StringComparison.Ordinal);

    private void OnServerUrlEdited()
    {
        if (_serverMessage.Tag is StatusTone.Error) ShowServerMessage(null, null);
        UpdateSaveButton();
    }

    private void UpdateSaveButton() =>
        _saveButton.Enabled = _status is not null && IsServerUrlEdited() && !string.IsNullOrWhiteSpace(_serverUrl.Text);

    private async Task SaveServerUrlAsync()
    {
        string url;
        try
        {
            url = AgentConfiguration.NormalizeServerUrl(_serverUrl.Text);
        }
        catch (ArgumentException)
        {
            ShowServerMessage("Введите полный адрес, начиная с http:// или https://", StatusTone.Error);
            _serverUrl.Inner.Focus();
            return;
        }

        _saveButton.Enabled = false;
        try
        {
            var response = await _pipe.SetServerUrlAsync(url);
            if (!response.Success)
            {
                ShowServerMessage($"Адрес не сохранён: {response.Error}", StatusTone.Error);
                return;
            }
            _serverUrlFromService = url;
            _serverUrl.Text = url;
            ShowServerMessage("Адрес сохранён — проверяем связь с сервером.", StatusTone.Success);
        }
        catch (Exception ex) when (ex is IOException or TimeoutException or UnauthorizedAccessException or OperationCanceledException or JsonException)
        {
            ShowServerMessage("Служба агента не отвечает — адрес не сохранён.", StatusTone.Error);
            return;
        }
        finally
        {
            UpdateSaveButton();
        }
        await SyncAsync();
    }

    /// <summary>Result or validation message under the address field; null text hides it.</summary>
    private void ShowServerMessage(string? text, StatusTone? tone)
    {
        _serverMessage.Text = text ?? string.Empty;
        _serverMessage.Tag = tone;
        _serverMessage.ForeColor = tone is { } value ? Theme.Current.ToneColor(value) : Theme.Current.TextSecondary;
        _serverMessage.Visible = !string.IsNullOrEmpty(text);
    }

    // ---- Journal --------------------------------------------------------------------------

    private async Task RefreshLogsAsync()
    {
        _refreshLogsButton.Enabled = false;
        try
        {
            using var cancellation = new CancellationTokenSource(PipeTimeout);
            var response = await _pipe.GetLogsAsync(LogLines, cancellation.Token);
            _logLines = response.Success ? response.DataAs<string[]>() ?? Array.Empty<string>() : null;
            _logMessage = response.Success ? null : response.Error;
        }
        catch (Exception ex) when (ex is IOException or TimeoutException or UnauthorizedAccessException or OperationCanceledException or JsonException)
        {
            // The service is down — exactly when the log matters most. Control runs elevated
            // and can read the file itself.
            _logLines = ReadLogFile();
            _logMessage = _logLines is null ? "Служба агента не отвечает, а файл журнала недоступен." : null;
        }
        finally
        {
            _refreshLogsButton.Enabled = true;
        }
        RenderLogs();
    }

    private static IReadOnlyList<string>? ReadLogFile()
    {
        try
        {
            var file = AgentConfiguration.LogFile();
            if (!File.Exists(file)) return Array.Empty<string>();
            using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream);
            var lines = new Queue<string>(LogLines);
            while (reader.ReadLine() is { } line)
            {
                if (lines.Count == LogLines) lines.Dequeue();
                lines.Enqueue(line);
            }
            return lines.ToArray();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private void RenderLogs()
    {
        var theme = Theme.Current;
        var rtf = new LogRtfBuilder(theme, _logs.Font);
        if (_logMessage is not null) rtf.Note(_logMessage, theme.Critical);
        else if (_logLines is null) rtf.Note("Загрузка…", theme.TextTertiary);
        else if (_logLines.Count == 0) rtf.Note("Журнал пока пуст.", theme.TextTertiary);
        else foreach (var line in _logLines) rtf.Line(line);
        _logs.Rtf = rtf.ToString();
        _logs.SelectionStart = _logs.TextLength;
        WindowChrome.ScrollToBottom(_logs);
    }

    private static void OpenLogFolder()
    {
        var folder = Path.GetDirectoryName(AgentConfiguration.LogFile())!;
        if (Directory.Exists(folder))
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{folder}\"") { UseShellExecute = true });
    }

    private static string FirstLine(string text)
    {
        var end = text.IndexOfAny(new[] { '\r', '\n' });
        return end < 0 ? text : text[..end];
    }

    /// <summary>
    /// Colours the agent log: "2026-10-09T14:32:01.123+05:00 [WARN] message" becomes a short
    /// local timestamp, a coloured level and the message; stack-trace lines are dimmed.
    /// </summary>
    private sealed partial class LogRtfBuilder
    {
        private const string ParagraphMark = "\\par\r\n";

        private readonly StringBuilder _body = new();
        private readonly List<Color> _colors = new();
        private readonly Theme _theme;
        private readonly Font _font;

        public LogRtfBuilder(Theme theme, Font font)
        {
            _theme = theme;
            _font = font;
        }

        public void Note(string text, Color color) => Run(text, color).Paragraph();

        public void Line(string line)
        {
            var match = LogLinePattern().Match(line);
            if (!match.Success)
            {
                Run("    " + line, _theme.TextTertiary).Paragraph();
                return;
            }
            var stamp = DateTimeOffset.TryParse(match.Groups["at"].Value, CultureInfo.InvariantCulture, DateTimeStyles.None, out var at)
                ? at.ToLocalTime().ToString("dd.MM HH:mm:ss", CultureInfo.InvariantCulture)
                : match.Groups["at"].Value;
            var level = match.Groups["level"].Value;
            var levelColor = level switch
            {
                "ERROR" => _theme.Critical,
                "WARN" => _theme.Caution,
                _ => _theme.TextTertiary
            };
            Run(stamp + "  ", _theme.TextTertiary)
                .Run(level.PadRight(6), levelColor)
                .Run(match.Groups["message"].Value, level == "ERROR" ? _theme.Critical : _theme.TextPrimary)
                .Paragraph();
        }

        public override string ToString()
        {
            var rtf = new StringBuilder(@"{\rtf1\ansi\deff0{\fonttbl{\f0\fmodern ");
            rtf.Append(_font.Name).Append(";}}{\\colortbl ;");
            foreach (var color in _colors) rtf.Append($"\\red{color.R}\\green{color.G}\\blue{color.B};");
            rtf.Append('}').Append($"\\f0\\fs{(int)Math.Round(_font.SizeInPoints * 2)}\\sl276\\slmult1 ");
            // No paragraph mark after the last line: the caret would sit on an empty line below
            // it, and scrolling to the caret would leave the newest entry half cut off.
            var body = _body.ToString();
            if (body.EndsWith(ParagraphMark, StringComparison.Ordinal)) body = body[..^ParagraphMark.Length];
            return rtf.Append(body).Append('}').ToString();
        }

        private LogRtfBuilder Run(string text, Color color)
        {
            var index = _colors.IndexOf(color);
            if (index < 0)
            {
                _colors.Add(color);
                index = _colors.Count - 1;
            }
            _body.Append("\\cf").Append(index + 1).Append(' ');
            foreach (var c in text)
            {
                switch (c)
                {
                    case '\\': _body.Append(@"\\"); break;
                    case '{': _body.Append(@"\{"); break;
                    case '}': _body.Append(@"\}"); break;
                    case '\t': _body.Append(@"\tab "); break;
                    case '\r' or '\n': break;
                    case < (char)0x80: _body.Append(c); break;
                    default: _body.Append("\\u").Append((short)c).Append('?'); break;
                }
            }
            return this;
        }

        private void Paragraph() => _body.Append(ParagraphMark);

        [GeneratedRegex(@"^(?<at>\S+) \[(?<level>[A-Z]+)\] (?<message>.*)$")]
        private static partial Regex LogLinePattern();
    }
}
