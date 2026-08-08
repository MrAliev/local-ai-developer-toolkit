using System.Reflection;
using CodeSearch.Mcp;
using LocalAi.Contracts;
using ModelContextProtocol.Server;

namespace CodeSearch.Tests;

/// <summary>
/// Holds the installer's idea of this server's tools to the server itself.
///
/// The installer configures clients without loading either MCP server, so the tool list it writes
/// per-tool rows from is a copy. A copy drifts silently — that is exactly how a machine ended up
/// with rows for eleven of twenty tools — so the drift is made a build failure rather than
/// something to notice later.
/// </summary>
public sealed class McpToolInventoryTests
{
    [Fact]
    public void The_installer_tool_list_matches_what_this_server_exposes()
    {
        var exposed = typeof(CodeSearchTools)
            .GetMethods(BindingFlags.Public | BindingFlags.Static)
            .Select(method => method.GetCustomAttribute<McpServerToolAttribute>())
            .Where(attribute => attribute is not null)
            .Select(attribute => attribute!.Name!)
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToArray();

        Assert.NotEmpty(exposed);
        Assert.Equal(
            exposed,
            McpToolNames.CodeSearch.OrderBy(name => name, StringComparer.Ordinal).ToArray());
    }
}
