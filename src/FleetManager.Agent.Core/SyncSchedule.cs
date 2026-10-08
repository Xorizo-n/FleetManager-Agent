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
    public static TimeSpan NextDelay(AgentOptions options)
    {
        var awaitingRegistration =
            !string.IsNullOrWhiteSpace(options.ServerUrl) &&
            string.IsNullOrWhiteSpace(options.AgentToken) &&
            !string.IsNullOrWhiteSpace(options.EnrollmentToken);
        return awaitingRegistration
            ? RegistrationRetry
            : TimeSpan.FromMinutes(Math.Clamp(options.SyncIntervalMinutes, 1, 1440));
    }
}
