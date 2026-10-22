using FleetManager.Agent.Core;
using FleetManager.Agent.Service;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

// Before anything opens the data folder: agents installed earlier left it with ProgramData's
// permissions, and any local user could read the agent token from agent.json.
IReadOnlyList<string> dataFolderFixes;
try { dataFolderFixes = DataFolderSecurity.Repair(AgentConfiguration.DefaultDataDirectory); }
catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
{
    dataFolderFixes = new[] { $"not checked: {ex.Message}" };
}

var builder = Host.CreateApplicationBuilder(args);
builder.Services.AddWindowsService(options => options.ServiceName = AgentConfiguration.ServiceName);
builder.Services.AddSingleton<AgentState>();
builder.Services.AddSingleton<AgentLogger>();
builder.Services.AddSingleton<IInventoryCollector, WindowsInventoryCollector>();
builder.Services.AddSingleton<ServerRouteKeeper>();
builder.Services.AddHostedService<AgentWorker>();
var host = builder.Build();
var logger = host.Services.GetRequiredService<AgentLogger>();
foreach (var fix in dataFolderFixes) logger.Warn($"Data folder permissions: {fix}");
await host.RunAsync();
