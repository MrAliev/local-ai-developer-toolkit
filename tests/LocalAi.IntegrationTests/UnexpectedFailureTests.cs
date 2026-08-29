using LocalAi.Cli;

namespace LocalAi.IntegrationTests;

public sealed class UnexpectedFailureTests
{
    /// <summary>
    /// The regression this helper exists for: a bare DllNotFoundException carries the
    /// default message "Dll was not found." and nothing else. The rendered line must name
    /// the type, because the message names nothing (#139).
    /// </summary>
    [Fact]
    public void A_bare_dll_not_found_exception_names_its_type()
    {
        var line = UnexpectedFailure.Describe(new DllNotFoundException());

        Assert.StartsWith("DllNotFoundException: ", line, StringComparison.Ordinal);
    }

    [Fact]
    public void The_inner_chain_is_rendered_outermost_first()
    {
        var line = UnexpectedFailure.Describe(new TypeInitializationException(
            "LibGit2Sharp.Core.NativeMethods",
            new DllNotFoundException("Unable to load DLL 'git2-example'.")));

        Assert.Contains("TypeInitializationException: ", line, StringComparison.Ordinal);
        Assert.EndsWith(
            " -> DllNotFoundException: Unable to load DLL 'git2-example'.",
            line,
            StringComparison.Ordinal);
    }

    [Fact]
    public void An_exception_without_inners_is_a_single_segment()
    {
        var line = UnexpectedFailure.Describe(new InvalidOperationException("broker gone"));

        Assert.Equal("InvalidOperationException: broker gone", line);
    }
}
