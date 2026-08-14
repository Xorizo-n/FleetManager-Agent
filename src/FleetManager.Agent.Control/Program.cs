using FleetManager.Agent.Core;
using System.Text.Json;

namespace FleetManager.Agent.Control;

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        ApplicationConfiguration.Initialize();
        Application.Run(new ControlForm());
    }
}

internal sealed class ControlForm : Form
{
    private readonly AgentPipeClient _pipe = new();
    private readonly TextBox _serverUrl = new() { Dock = DockStyle.Fill };
    private readonly Label _status = new() { AutoSize = true, Text = "Состояние: проверка..." };
    private readonly RichTextBox _logs = new() { Dock = DockStyle.Fill, ReadOnly = true, BackColor = Color.White };

    public ControlForm()
    {
        Text = "FleetManager Agent — управление";
        Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath);
        Width = 760;
        Height = 520;
        StartPosition = FormStartPosition.CenterScreen;
        var serverLabel = new Label { Text = "Адрес сервера:", AutoSize = true, Anchor = AnchorStyles.Left };
        var save = new Button { Text = "Сохранить", AutoSize = true };
        var sync = new Button { Text = "Синхронизировать", AutoSize = true };
        var refresh = new Button { Text = "Обновить", AutoSize = true };
        save.Click += async (_, _) => await SaveAsync();
        sync.Click += async (_, _) => await SyncAsync();
        refresh.Click += async (_, _) => await RefreshAsync();
        var top = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, Padding = new Padding(10), ColumnCount = 4 };
        top.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        top.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        top.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        top.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        top.Controls.Add(serverLabel, 0, 0); top.Controls.Add(_serverUrl, 1, 0); top.Controls.Add(save, 2, 0); top.Controls.Add(sync, 3, 0);
        var statusPanel = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, Padding = new Padding(10, 0, 10, 10) };
        statusPanel.Controls.Add(_status); statusPanel.Controls.Add(refresh);
        Controls.Add(_logs); Controls.Add(statusPanel); Controls.Add(top);
        Shown += async (_, _) => await RefreshAsync();
    }

    private async Task RefreshAsync()
    {
        try
        {
            var status = await _pipe.GetStatusAsync();
            if (status.Success)
            {
                var json = JsonSerializer.Serialize(status.Data, JsonDefaults.Options);
                var agentStatus = JsonSerializer.Deserialize<AgentStatus>(json, JsonDefaults.Options);
                if (agentStatus is not null)
                {
                    _serverUrl.Text = agentStatus.ServerUrl;
                    _status.Text = $"Состояние: {agentStatus.State}; последняя синхронизация: {agentStatus.LastSyncAt?.ToLocalTime():g}";
                }
                else _status.Text = "Состояние: " + json;
                var logs = await _pipe.GetLogsAsync();
                _logs.Text = logs.Success ? JsonSerializer.Deserialize<string[]>(JsonSerializer.Serialize(logs.Data, JsonDefaults.Options)) is { } values ? string.Join(Environment.NewLine, values) : string.Empty : logs.Error;
            }
            else _status.Text = "Ошибка: " + status.Error;
        }
        catch (Exception ex) { _status.Text = "Служба недоступна: " + ex.Message; }
    }

    private async Task SaveAsync()
    {
        try { var response = await _pipe.SetServerUrlAsync(_serverUrl.Text); _status.Text = response.Success ? "Адрес сохранён." : "Ошибка: " + response.Error; }
        catch (Exception ex) { _status.Text = ex.Message; }
    }

    private async Task SyncAsync()
    {
        try { var response = await _pipe.SyncAsync(); _status.Text = response.Success ? "Синхронизация запущена." : "Ошибка: " + response.Error; await RefreshAsync(); }
        catch (Exception ex) { _status.Text = ex.Message; }
    }
}
