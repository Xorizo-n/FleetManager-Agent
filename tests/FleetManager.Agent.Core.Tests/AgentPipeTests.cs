using FleetManager.Agent.Core;
using Xunit;

namespace FleetManager.Agent.Core.Tests;

public sealed class AgentPipeTests
{
    [Fact]
    public async Task Status_isAnsweredWhileASyncHoldsItsConnection()
    {
        // Регрессия: pipe-сервер обслуживал одного клиента за раз, и пока «Синхронизировать сейчас»
        // ждал окончания синхронизации, трей не мог получить статус и писал, что служба недоступна.
        if (!OperatingSystem.IsWindows()) return;

        var directory = Path.Combine(Path.GetTempPath(), "fleet-manager-agent-tests", Guid.NewGuid().ToString("N"));
        var pipeName = "FleetManagerAgent.Tests." + Guid.NewGuid().ToString("N");
        using var stopping = new CancellationTokenSource();
        var syncStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseSync = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            var state = new AgentState(directory);
            state.MarkRunning();
            var server = new AgentPipeServer(state, new AgentLogger(directory), async cancellationToken =>
            {
                state.MarkSyncStarted();
                syncStarted.TrySetResult();
                await releaseSync.Task.WaitAsync(cancellationToken);
                state.MarkSyncFinished();
            }, stopping.Token, pipeName);
            var serverTask = server.RunAsync();
            var client = new AgentPipeClient(pipeName);

            var sync = client.SyncAsync();
            await syncStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));

            var status = await client.TryGetStatusAsync(TimeSpan.FromSeconds(5));
            Assert.NotNull(status);
            Assert.True(status.IsSyncing);

            releaseSync.SetResult();
            var response = await sync.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.True(response.Success);
            Assert.False(response.DataAs<AgentStatus>()!.IsSyncing);

            stopping.Cancel();
            await serverTask.WaitAsync(TimeSpan.FromSeconds(10));
        }
        finally
        {
            releaseSync.TrySetResult();
            stopping.Cancel();
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task TryGetStatus_returnsNullWhenNoServiceListens()
    {
        if (!OperatingSystem.IsWindows()) return;
        var client = new AgentPipeClient("FleetManagerAgent.Tests." + Guid.NewGuid().ToString("N"));
        Assert.Null(await client.TryGetStatusAsync(TimeSpan.FromSeconds(5)));
    }
}
