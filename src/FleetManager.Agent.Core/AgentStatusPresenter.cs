using System.Globalization;

namespace FleetManager.Agent.Core;

public enum StatusTone
{
    Neutral,
    Success,
    Busy,
    Warning,
    Error
}

/// <param name="Title">Short state, e.g. "Нет связи с сервером".</param>
/// <param name="Detail">One line of context: when the last synchronization happened, what to do.</param>
/// <param name="Error">Raw error text from the service, for the control panel only.</param>
public sealed record StatusSummary(StatusTone Tone, string Title, string? Detail = null, string? Error = null);

/// <summary>
/// Human-readable agent state shared by Tray and Control, so both always say the same thing.
/// </summary>
public static class AgentStatusPresenter
{
    public const string ProductTitle = "FleetManager Agent";

    /// <summary>NotifyIcon.Text throws ArgumentOutOfRangeException above 127 characters.</summary>
    public const int TooltipMaxLength = 127;

    public static readonly StatusSummary ServiceUnavailable = new(
        StatusTone.Error, "Служба агента не отвечает", "Проверьте, что служба FleetManagerAgent запущена.");

    /// <param name="status">Status from the service, or null when the service did not answer.</param>
    public static StatusSummary Describe(AgentStatus? status, DateTimeOffset now)
    {
        if (status is null) return ServiceUnavailable;

        switch (status.State)
        {
            case AgentLifecycleState.Starting:
                return new StatusSummary(StatusTone.Neutral, "Служба запускается");
            case AgentLifecycleState.Stopping or AgentLifecycleState.Offline:
                return new StatusSummary(StatusTone.Neutral, "Служба останавливается");
        }

        if (status.IsSyncing)
            return new StatusSummary(StatusTone.Busy, "Синхронизация…", LastSync(status, now));

        return status.Issue switch
        {
            AgentIssue.ServerNotConfigured => new StatusSummary(StatusTone.Warning, "Адрес сервера не задан",
                "Укажите его в панели управления."),
            AgentIssue.NotEnrolled => new StatusSummary(StatusTone.Warning, "Агент не зарегистрирован",
                "Нет токена регистрации: переустановите агент с /EnrollmentToken."),
            AgentIssue.AwaitingReboot => new StatusSummary(StatusTone.Warning, "Ожидает перезагрузки",
                "Агент зарегистрируется после перезагрузки под новым именем ПК."),
            AgentIssue.ServerUnreachable => Failure("Нет связи с сервером", status, now),
            AgentIssue.ServerRejected => Failure("Сервер отклонил запрос", status, now),
            AgentIssue.Failed => Failure("Ошибка синхронизации", status, now),
            _ when status.State == AgentLifecycleState.Degraded => Failure("Ошибка синхронизации", status, now),
            _ when status.LastSyncAt is null => new StatusSummary(StatusTone.Neutral, "Ожидание первой синхронизации"),
            _ => new StatusSummary(StatusTone.Success, "Подключено к серверу", LastSync(status, now))
        };
    }

    /// <summary>"Следующая синхронизация сегодня в 14:37", when it is scheduled.</summary>
    public static string? NextSync(AgentStatus? status, DateTimeOffset now) =>
        status is { IsSyncing: false, NextSyncAt: { } next } && next > now
            ? $"Следующая синхронизация {FormatMoment(next, now)}"
            : null;

    /// <summary>Tray tooltip: product, state and one line of context, within the NotifyIcon limit.</summary>
    public static string Tooltip(StatusSummary summary)
    {
        var text = $"{ProductTitle}\n{summary.Title}";
        if (!string.IsNullOrWhiteSpace(summary.Detail)) text += "\n" + summary.Detail;
        return Truncate(text, TooltipMaxLength);
    }

    /// <summary>"сегодня в 14:32", "вчера в 09:05" or "07.10.2026 в 18:40", in local time.</summary>
    public static string FormatMoment(DateTimeOffset moment, DateTimeOffset now)
    {
        var local = moment.ToLocalTime();
        var today = now.ToLocalTime().Date;
        var time = local.ToString("HH:mm", CultureInfo.InvariantCulture);
        if (local.Date == today) return $"сегодня в {time}";
        if (local.Date == today.AddDays(-1)) return $"вчера в {time}";
        if (local.Date == today.AddDays(1)) return $"завтра в {time}";
        return $"{local.ToString("dd.MM.yyyy", CultureInfo.InvariantCulture)} в {time}";
    }

    public static string Truncate(string text, int maxLength) =>
        text.Length <= maxLength ? text : text[..(maxLength - 1)].TrimEnd() + "…";

    private static StatusSummary Failure(string title, AgentStatus status, DateTimeOffset now) => new(
        StatusTone.Error,
        title,
        status.LastSyncAt is { } at
            ? $"Последняя успешная синхронизация {FormatMoment(at, now)}"
            : "Успешных синхронизаций ещё не было",
        status.LastError);

    private static string? LastSync(AgentStatus status, DateTimeOffset now) =>
        status.LastSyncAt is { } at ? $"Синхронизировано {FormatMoment(at, now)}" : null;
}
