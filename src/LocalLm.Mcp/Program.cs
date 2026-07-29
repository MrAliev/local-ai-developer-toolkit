using System.Text;
using LocalLm.Core;
using LocalLm.Mcp;
using LocalAi.Broker.Client;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

// stdio transport: stdout carries JSON-RPC frames only, so all logging goes to stderr.
var builder = Host.CreateApplicationBuilder(args);
builder.Logging.AddConsole(options => options.LogToStandardErrorThreshold = LogLevel.Trace);

try
{
    Console.OutputEncoding = Encoding.UTF8;
}
catch (IOException)
{
    // No console attached - the pipe is UTF-8 already.
}

builder.Services.AddSingleton<ILocalModelClient>(
    new BrokerLocalModelClient(BrokerClientFactory.CreateDefault()));
builder.Services.AddSingleton<LocalTasks>();
builder.Services.AddSingleton<ModelManagementTasks>();
builder.Services.AddHostedService<RecommendedModelSyncService>();

builder.Services
    .AddMcpServer()
    .WithStdioServerTransport()
    .WithToolsFromAssembly();

await builder.Build().RunAsync();
