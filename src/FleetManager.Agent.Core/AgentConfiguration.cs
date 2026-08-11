namespace FleetManager.Agent.Core;

/// <summary>
/// Paths and validation rules shared by the service, tray and elevated control UI.
/// </summary>
public static class AgentConfiguration
{
    public const string ProductName = "FleetManagerAgent";
    public const string PipeName = "FleetManagerAgent";
    public const string DefaultApiPath = "/api/agent";

    public static string DefaultDataDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
        ProductName);

    public static string ConfigurationFile(string? dataDirectory = null) =>
        Path.Combine(dataDirectory ?? DefaultDataDirectory, "agent.json");

    public static string MachineIdentityFile(string? dataDirectory = null) =>
        Path.Combine(dataDirectory ?? DefaultDataDirectory, "machine-id");

    public static string LogFile(string? dataDirectory = null) =>
        Path.Combine(dataDirectory ?? DefaultDataDirectory, "logs", "agent.log");

    public static string NormalizeServerUrl(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new ArgumentException("Server URL is required.", nameof(value));
        }

        var normalized = value.Trim().TrimEnd('/');
        if (!Uri.TryCreate(normalized, UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            throw new ArgumentException("Server URL must be an absolute HTTP or HTTPS URL.", nameof(value));
        }

        return normalized;
    }
}
