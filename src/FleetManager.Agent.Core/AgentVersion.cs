using System.Reflection;

namespace FleetManager.Agent.Core;

/// <summary>
/// Версия агента, которую служба сообщает серверу в register/heartbeat —
/// Fleet Manager показывает её в реестре хостов и по ней решает, нужно ли
/// обновление.
///
/// Номер проставляется при сборке: build-installer.ps1 передаёт один и тот же
/// AppVersion и в dotnet publish (-p:Version), и в Inno Setup (/DAppVersion).
/// Поэтому версия из heartbeat совпадает с DisplayVersion записи удаления
/// Windows, которую сервер читает при проверке по SSH.
/// </summary>
public static class AgentVersion
{
    private static readonly Lazy<string?> Resolved = new(Resolve, LazyThreadSafetyMode.PublicationOnly);

    /// <summary>Версия текущей сборки агента или null, если её не удалось определить.</summary>
    public static string? Current => Resolved.Value;

    /// <summary>Отбрасывает метаданные сборки ("1.2.3+sha") и пустые значения.</summary>
    public static string? Normalize(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;

        var trimmed = value.Trim();
        var metadata = trimmed.IndexOf('+');
        if (metadata >= 0) trimmed = trimmed[..metadata];

        trimmed = trimmed.Trim();
        return trimmed.Length == 0 ? null : trimmed;
    }

    private static string? Resolve()
    {
        var assembly = Assembly.GetEntryAssembly() ?? typeof(AgentVersion).Assembly;
        // InformationalVersion сохраняет строку версии как есть ("2025.08.14.12"),
        // тогда как AssemblyVersion уже нормализован ("2025.8.14.12").
        return Normalize(assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion)
               ?? Normalize(assembly.GetName().Version?.ToString());
    }
}
