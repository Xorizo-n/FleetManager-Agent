namespace FleetManager.Agent.Core;

public static class SyncSchedule
{
    /// <summary>Pause before the next attempt while the agent has an enrollment token but no registration yet.</summary>
    public static readonly TimeSpan RegistrationRetry = TimeSpan.FromMinutes(1);

    /// <summary>
    /// Delay before the next synchronization. An agent waiting to register retries every minute:
    /// right after boot the network is often not up yet, and the regular interval would leave a
    /// freshly provisioned PC out of Fleet Manager for several minutes.
    /// </summary>
    public static TimeSpan NextDelay(AgentOptions options) => NextDelay(options, 0);

    /// <summary>Retries after the server did not answer: the first heartbeat after boot often runs before the network is up.</summary>
    public static readonly TimeSpan[] FailureRetries =
        [TimeSpan.FromSeconds(15), TimeSpan.FromSeconds(30), TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(2)];

    /// <summary>
    /// <paramref name="failures"/> — heartbeats in a row the server did not get. Then the next attempt comes soon
    /// (15 s, 30 s, 1 min, 2 min), never later than the regular interval: a PC whose network came up a minute after
    /// the service no longer stays offline for a whole interval.
    /// </summary>
    public static TimeSpan NextDelay(AgentOptions options, int failures)
    {
        var awaitingRegistration =
            !string.IsNullOrWhiteSpace(options.ServerUrl) &&
            string.IsNullOrWhiteSpace(options.AgentToken) &&
            !string.IsNullOrWhiteSpace(options.EnrollmentToken);
        if (awaitingRegistration) return RegistrationRetry;
        var interval = TimeSpan.FromMinutes(Math.Clamp(options.SyncIntervalMinutes, 1, 1440));
        if (failures <= 0) return interval;
        var retry = FailureRetries[Math.Min(failures, FailureRetries.Length) - 1];
        return retry < interval ? retry : interval;
    }
}
