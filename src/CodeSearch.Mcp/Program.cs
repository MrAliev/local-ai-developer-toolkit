using System.Text;
using CodeSearch.Core.Search;
using LocalAi.Contracts.Localization;
using CodeSearch.Core.Semantics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

// The language every line below is written in, decided before the first one is. Numbers stay
// invariant whatever the language is; only the words move.
OutputCulture.Apply();

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
builder.Services.AddSingleton(new LanguageServerPolicyStore(
    LanguageServerPolicyStore.DefaultRuntimeRoot));
builder.Services.AddSingleton(new HeuristicNavigationPolicyStore(
    HeuristicNavigationPolicyStore.DefaultRuntimeRoot));
builder.Services.AddSingleton(serviceProvider =>
{
    var policyStore = serviceProvider.GetRequiredService<LanguageServerPolicyStore>();
    return new LanguageServerSessionManager((workspaceRoot, languageId) =>
        StdioLanguageServerClient.Start(
            workspaceRoot,
            policyStore.Read().ProcessSpec(languageId)));
});
builder.Services.AddSingleton<ILiveSemanticNavigation, LiveSemanticNavigationOverlay>();
builder.Services.AddSingleton<IHeuristicSemanticNavigation, HeuristicSemanticNavigation>();
builder.Services.AddSingleton<SemanticNavigationGateway>();

builder.Services
    .AddMcpServer()
    .WithStdioServerTransport()
    .WithToolsFromAssembly();

await builder.Build().RunAsync();
