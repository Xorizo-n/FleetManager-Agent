namespace FleetManager.Agent.Core;

public sealed class AgentLogger
{
    private readonly object _gate = new();
    private readonly string _file;

    public AgentLogger(string? dataDirectory = null)
    {
        _file = AgentConfiguration.LogFile(dataDirectory);
        Directory.CreateDirectory(Path.GetDirectoryName(_file)!);
    }

    public void Info(string message) => Write("INFO", message);
    public void Warn(string message) => Write("WARN", message);
    public void Error(string message, Exception? exception = null) => Write("ERROR", exception is null ? message : $"{message}: {exception}");

    public IReadOnlyList<string> Tail(int maxLines = 200)
    {
        if (!File.Exists(_file)) return Array.Empty<string>();
        lock (_gate)
        {
            return File.ReadLines(_file).TakeLast(Math.Max(1, maxLines)).ToArray();
        }
    }

    private void Write(string level, string message)
    {
        lock (_gate)
        {
            File.AppendAllText(_file, $"{DateTimeOffset.Now:O} [{level}] {message}{Environment.NewLine}");
        }
    }
}
