using System.Text;
using CodeSearch.Core.Search;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

// stdio transport means stdout carries JSON-RPC frames and nothing else. Any stray Console.Write
// corrupts the protocol, so logging is pinned to stderr.
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

builder.Services.AddSingleton(new SearchService());

builder.Services
    .AddMcpServer()
    .WithStdioServerTransport()
    .WithToolsFromAssembly();

await builder.Build().RunAsync();
