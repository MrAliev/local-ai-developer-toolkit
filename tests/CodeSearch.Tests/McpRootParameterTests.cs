using System.Reflection;
using CodeSearch.Core.Semantics;
using CodeSearch.Mcp;
using ModelContextProtocol.Server;

namespace CodeSearch.Tests;

/// <summary>
/// One server, one contract for the repository root.
///
/// Every tool here resolves an omitted root to the repository containing the working
/// directory — except that the two LSP tools once required it, so a caller who omitted it
/// exactly as they may everywhere else got the MCP layer's bare "An error occurred invoking
/// 'lsp_open_document'." with nothing naming the missing parameter (#238). The rule is
/// checked by reflection rather than by remembering it, because the next tool added to this
/// class is the one that would break it again.
/// </summary>
public sealed class McpRootParameterTests
{
    [Fact]
    public void Every_tool_that_takes_a_root_lets_the_caller_leave_it_out()
    {
        var required = typeof(CodeSearchTools)
            .GetMethods(BindingFlags.Public | BindingFlags.Static)
            .Where(method => method.GetCustomAttribute<McpServerToolAttribute>() is not null)
            .SelectMany(method => method
                .GetParameters()
                .Where(parameter =>
                    parameter.Name == "root" &&
                    !parameter.IsOptional)
                .Select(_ => method.GetCustomAttribute<McpServerToolAttribute>()!.Name!))
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToArray();

        Assert.Empty(required);
    }

    /// <summary>
    /// An optional parameter whose description does not say what omitting it does is optional
    /// only to the compiler: the caller reading the tool list still cannot tell. The wording
    /// is left to each tool — search_code has a worktree caveat worth stating, get_code_chunk
    /// a snapshot one — but every one of them has to answer the question.
    /// </summary>
    [Fact]
    public void The_root_parameter_says_what_omitting_it_means()
    {
        var descriptions = typeof(CodeSearchTools)
            .GetMethods(BindingFlags.Public | BindingFlags.Static)
            .Where(method => method.GetCustomAttribute<McpServerToolAttribute>() is not null)
            .SelectMany(method => method
                .GetParameters()
                .Where(parameter => parameter.Name == "root")
                .Select(parameter => (
                    Tool: method.GetCustomAttribute<McpServerToolAttribute>()!.Name!,
                    Description: parameter
                        .GetCustomAttribute<System.ComponentModel.DescriptionAttribute>()
                        ?.Description ?? string.Empty)))
            .ToArray();

        Assert.NotEmpty(descriptions);
        var silent = descriptions
            .Where(entry => !entry.Description.Contains(
                "Defaults to",
                StringComparison.Ordinal))
            .Select(entry => entry.Tool)
            .ToArray();

        Assert.True(
            silent.Length == 0,
            "These tools take an optional root without saying what omitting it does: " +
            string.Join(", ", silent));
    }

    /// <summary>
    /// The failure this fixes, from the caller's side: omitting the root has to produce an
    /// answer somebody can act on. On a default installation that answer is that live
    /// language servers are switched off — readable is the point, not successful.
    /// </summary>
    [Fact]
    public async Task Opening_a_document_without_a_root_answers_instead_of_faulting()
    {
        using var sessions = new DisabledSessions();

        var opened = await CodeSearchTools.LspOpenDocument(
            sessions.Manager,
            "src/Program.cs",
            "csharp",
            1,
            "class Program { }");
        var closed = await CodeSearchTools.LspCloseDocument(
            sessions.Manager,
            "src/Program.cs");

        Assert.Equal(
            "lsp_open_document failed: Live language-server integration is disabled.",
            opened);
        // Closing a document that was never opened is not an error anywhere else either.
        Assert.Equal("LSP document closed: src/Program.cs.", closed);
    }

    /// <summary>
    /// A session manager configured the way a default installation is: language servers are
    /// off, so asking for one says so rather than starting anything.
    /// </summary>
    private sealed class DisabledSessions : IDisposable
    {
        public LanguageServerSessionManager Manager { get; } = new((_, _) =>
            throw new InvalidOperationException(
                "Live language-server integration is disabled."));

        public void Dispose() => Manager.DisposeAsync().AsTask().GetAwaiter().GetResult();
    }
}
