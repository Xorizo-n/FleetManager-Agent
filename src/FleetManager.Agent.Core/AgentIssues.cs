using System.Net.Sockets;

namespace FleetManager.Agent.Core;

/// <summary>A synchronization failure whose cause is already known.</summary>
public sealed class AgentIssueException : InvalidOperationException
{
    public AgentIssueException(AgentIssue issue, string message) : base(message) => Issue = issue;

    public AgentIssue Issue { get; }
}

public static class AgentIssues
{
    /// <summary>Maps a synchronization failure to the cause shown to the user.</summary>
    public static AgentIssue Classify(Exception exception) => exception switch
    {
        AgentIssueException known => known.Issue,
        HttpRequestException { StatusCode: not null } => AgentIssue.ServerRejected,
        HttpRequestException or SocketException or TimeoutException => AgentIssue.ServerUnreachable,
        // HttpClient reports its own timeout as a cancellation; a real shutdown is filtered out before.
        TaskCanceledException => AgentIssue.ServerUnreachable,
        _ when exception.InnerException is { } inner => Classify(inner),
        _ => AgentIssue.Failed
    };
}
