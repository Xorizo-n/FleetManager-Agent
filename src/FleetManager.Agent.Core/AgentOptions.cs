using System.Text.Json;

namespace FleetManager.Agent.Core;

public sealed class AgentOptions
{
    public string ServerUrl { get; set; } = string.Empty;
    public string? EnrollmentToken { get; set; }
    public string? AgentId { get; set; }
    public string? AgentToken { get; set; }
    public string? SshPublicKey { get; set; }
    public string? SshLogin { get; set; }
    public int SyncIntervalMinutes { get; set; } = 5;

    public static AgentOptions Load(string? dataDirectory = null)
    {
        var file = AgentConfiguration.ConfigurationFile(dataDirectory);
        if (!File.Exists(file))
        {
            return new AgentOptions();
        }

        try
        {
            return JsonSerializer.Deserialize<AgentOptions>(File.ReadAllText(file), JsonDefaults.Options)
                   ?? new AgentOptions();
        }
        catch
        {
            return new AgentOptions();
        }
    }

    public void Save(string? dataDirectory = null)
    {
        if (!string.IsNullOrWhiteSpace(ServerUrl))
        {
            ServerUrl = AgentConfiguration.NormalizeServerUrl(ServerUrl);
        }

        var file = AgentConfiguration.ConfigurationFile(dataDirectory);
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        File.WriteAllText(file, JsonSerializer.Serialize(this, JsonDefaults.Options));
    }
}

public static class JsonDefaults
{
    // WriteIndented must stay false: AgentPipe.cs frames pipe messages as a single
    // line (WriteLineAsync/ReadLineAsync), and indented JSON contains embedded
    // newlines that truncate the message to its first line ("{") on the reading
    // side, causing a JsonException ("Expected depth to be zero...").
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true
    };
}
