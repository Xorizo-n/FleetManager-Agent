using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using FleetManager.Agent.Core;
using Xunit;

namespace FleetManager.Agent.Core.Tests;

public sealed class AgentStatusPresenterTests
{
    private static readonly DateTimeOffset Now = Local(2026, 10, 9, 15, 0);

    private static DateTimeOffset Local(int year, int month, int day, int hour, int minute)
    {
        var wallClock = new DateTime(year, month, day, hour, minute, 0, DateTimeKind.Unspecified);
        return new DateTimeOffset(wallClock, TimeZoneInfo.Local.GetUtcOffset(wallClock));
    }

    private static AgentStatus Status(
        AgentLifecycleState state = AgentLifecycleState.Running,
        DateTimeOffset? lastSyncAt = null,
        string? lastError = null,
        AgentIssue issue = AgentIssue.None,
        bool isSyncing = false) =>
        new("3f2a8c1e-0000-0000-0000-000000000000", "SU5-E302-OP", state, Now.AddHours(-6), lastSyncAt,
            lastError is null ? null : Now, lastError, "http://10.40.240.154:8080", 214,
            "e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855", issue, isSyncing);

    [Fact]
    public void Describe_reportsUnavailableServiceWhenThereIsNoStatus()
    {
        var summary = AgentStatusPresenter.Describe(null, Now);
        Assert.Equal(StatusTone.Error, summary.Tone);
        Assert.Equal("Служба агента не отвечает", summary.Title);
    }

    [Fact]
    public void Describe_reportsConnectedAfterSuccessfulSync()
    {
        var summary = AgentStatusPresenter.Describe(Status(lastSyncAt: Local(2026, 10, 9, 14, 32)), Now);
        Assert.Equal(StatusTone.Success, summary.Tone);
        Assert.Equal("Подключено к серверу", summary.Title);
        Assert.Equal("Синхронизировано сегодня в 14:32", summary.Detail);
    }

    [Fact]
    public void Describe_keepsLastSuccessfulSyncWhenServerIsUnreachable()
    {
        var status = Status(AgentLifecycleState.Degraded, Local(2026, 10, 8, 9, 5), "No such host is known.", AgentIssue.ServerUnreachable);
        var summary = AgentStatusPresenter.Describe(status, Now);
        Assert.Equal(StatusTone.Error, summary.Tone);
        Assert.Equal("Нет связи с сервером", summary.Title);
        Assert.Equal("Последняя успешная синхронизация вчера в 09:05", summary.Detail);
        Assert.Equal("No such host is known.", summary.Error);
    }

    [Theory]
    [InlineData(AgentIssue.ServerNotConfigured, StatusTone.Warning, "Адрес сервера не задан")]
    [InlineData(AgentIssue.NotEnrolled, StatusTone.Warning, "Агент не зарегистрирован")]
    [InlineData(AgentIssue.AwaitingReboot, StatusTone.Warning, "Ожидает перезагрузки")]
    [InlineData(AgentIssue.ServerRejected, StatusTone.Error, "Сервер отклонил запрос")]
    [InlineData(AgentIssue.Failed, StatusTone.Error, "Ошибка синхронизации")]
    public void Describe_namesTheIssue(AgentIssue issue, StatusTone tone, string title)
    {
        var summary = AgentStatusPresenter.Describe(Status(lastSyncAt: Now.AddMinutes(-3), issue: issue), Now);
        Assert.Equal(tone, summary.Tone);
        Assert.Equal(title, summary.Title);
    }

    [Fact]
    public void Describe_showsSyncInProgressOverAPreviousError()
    {
        var status = Status(AgentLifecycleState.Degraded, lastError: "timeout", issue: AgentIssue.ServerUnreachable, isSyncing: true);
        Assert.Equal(StatusTone.Busy, AgentStatusPresenter.Describe(status, Now).Tone);
    }

    [Fact]
    public void Describe_waitsForFirstSyncInsteadOfClaimingConnection()
    {
        var summary = AgentStatusPresenter.Describe(Status(), Now);
        Assert.Equal(StatusTone.Neutral, summary.Tone);
    }

    [Fact]
    public void Tooltip_alwaysFitsTheNotifyIconLimit()
    {
        // Регрессия: трей клал в NotifyIcon.Text весь JSON статуса, сеттер бросал исключение
        // из-за длины, и подсказка всегда показывала, что служба недоступна.
        var longError = new string('x', 2000);
        var statuses = new AgentStatus?[]
        {
            null,
            Status(lastSyncAt: Now.AddMinutes(-1)),
            Status(AgentLifecycleState.Degraded, null, longError, AgentIssue.ServerRejected),
            Status(issue: AgentIssue.NotEnrolled),
            Status(issue: AgentIssue.AwaitingReboot)
        };
        foreach (var status in statuses)
        {
            var tooltip = AgentStatusPresenter.Tooltip(AgentStatusPresenter.Describe(status, Now));
            Assert.InRange(tooltip.Length, 1, AgentStatusPresenter.TooltipMaxLength);
            Assert.StartsWith(AgentStatusPresenter.ProductTitle, tooltip);
            Assert.DoesNotContain("{", tooltip);
        }
    }

    [Fact]
    public void Tooltip_truncatesWithEllipsis()
    {
        var tooltip = AgentStatusPresenter.Tooltip(new StatusSummary(StatusTone.Error, "Заголовок", new string('д', 300)));
        Assert.Equal(AgentStatusPresenter.TooltipMaxLength, tooltip.Length);
        Assert.EndsWith("…", tooltip);
    }

    [Theory]
    [InlineData(2026, 10, 9, 8, 1, "сегодня в 08:01")]
    [InlineData(2026, 10, 8, 23, 59, "вчера в 23:59")]
    [InlineData(2026, 10, 10, 0, 5, "завтра в 00:05")]
    [InlineData(2026, 9, 30, 18, 40, "30.09.2026 в 18:40")]
    public void FormatMoment_isRelativeToToday(int year, int month, int day, int hour, int minute, string expected)
    {
        Assert.Equal(expected, AgentStatusPresenter.FormatMoment(Local(year, month, day, hour, minute), Now));
    }

    [Fact]
    public void NextSync_isShownOnlyWhenScheduledAndIdle()
    {
        var scheduled = Status(lastSyncAt: Now.AddMinutes(-2)) with { NextSyncAt = Local(2026, 10, 9, 15, 3) };
        Assert.Equal("Следующая синхронизация сегодня в 15:03", AgentStatusPresenter.NextSync(scheduled, Now));
        Assert.Null(AgentStatusPresenter.NextSync(scheduled with { IsSyncing = true }, Now));
        Assert.Null(AgentStatusPresenter.NextSync(null, Now));
    }

    [Fact]
    public void PipeResponse_dataArrivesAsTypedStatus()
    {
        // Так статус приходит клиенту: Data — это JsonElement, а не AgentStatus.
        var sent = Status(lastSyncAt: Now, issue: AgentIssue.ServerRejected, isSyncing: true) with { NextSyncAt = Now.AddMinutes(5), AgentVersion = "2026.10.09.5" };
        var wire = JsonSerializer.Serialize(new AgentPipeResponse(true, Data: sent), JsonDefaults.Options);
        var received = JsonSerializer.Deserialize<AgentPipeResponse>(wire, JsonDefaults.Options)!;

        Assert.IsType<JsonElement>(received.Data);
        Assert.Equal(sent, received.DataAs<AgentStatus>());
        Assert.Null(new AgentPipeResponse(false, "error").DataAs<AgentStatus>());
    }

    [Fact]
    public void Classify_separatesNetworkFailuresFromServerErrors()
    {
        Assert.Equal(AgentIssue.ServerRejected, AgentIssues.Classify(new HttpRequestException("401", null, HttpStatusCode.Unauthorized)));
        Assert.Equal(AgentIssue.ServerUnreachable, AgentIssues.Classify(new HttpRequestException("No such host is known.")));
        Assert.Equal(AgentIssue.ServerUnreachable, AgentIssues.Classify(new TaskCanceledException("HttpClient.Timeout")));
        Assert.Equal(AgentIssue.ServerUnreachable, AgentIssues.Classify(new InvalidOperationException("wrapped", new SocketException())));
        Assert.Equal(AgentIssue.NotEnrolled, AgentIssues.Classify(new AgentIssueException(AgentIssue.NotEnrolled, "Enrollment token is not configured.")));
        Assert.Equal(AgentIssue.Failed, AgentIssues.Classify(new InvalidOperationException("PowerShell failed")));
    }

    [Fact]
    public void AgentState_tracksIssueThroughErrorAndRecovery()
    {
        var directory = Path.Combine(Path.GetTempPath(), "fleet-manager-agent-tests", Guid.NewGuid().ToString("N"));
        try
        {
            var state = new AgentState(directory);
            state.MarkRunning();
            state.MarkError(new HttpRequestException("Connection refused"));
            Assert.Equal(AgentLifecycleState.Degraded, state.Status.State);
            Assert.Equal(AgentIssue.ServerUnreachable, state.Status.Issue);

            state.MarkSync(DateTimeOffset.UtcNow, 10, null);
            Assert.Equal(AgentLifecycleState.Running, state.Status.State);
            Assert.Equal(AgentIssue.None, state.Status.Issue);
            Assert.Null(state.Status.LastError);

            state.MarkWaiting(AgentIssue.AwaitingReboot);
            Assert.Equal(AgentIssue.AwaitingReboot, state.Status.Issue);
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }
}
