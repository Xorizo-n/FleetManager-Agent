using FleetManager.Agent.Core;
using FleetManager.Agent.Service;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

var builder = Host.CreateApplicationBuilder(args);
builder.Services.AddWindowsService(options => options.ServiceName = "FleetManagerAgent");
builder.Services.AddSingleton<AgentState>();
builder.Services.AddSingleton<AgentLogger>();
builder.Services.AddSingleton<IInventoryCollector, WindowsInventoryCollector>();
builder.Services.AddHostedService<AgentWorker>();
await builder.Build().RunAsync();
