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
        var line = UnexpectedFailure.Describe(new DllNotFoundException(), stackSwitch: null);

        Assert.StartsWith("DllNotFoundException: ", line, StringComparison.Ordinal);
    }

    [Fact]
    public void The_inner_chain_is_rendered_outermost_first()
    {
        var line = UnexpectedFailure.Describe(
            new TypeInitializationException(
                "LibGit2Sharp.Core.NativeMethods",
                new DllNotFoundException("Unable to load DLL 'git2-example'.")),
            stackSwitch: null);

        Assert.Contains("TypeInitializationException: ", line, StringComparison.Ordinal);
        Assert.EndsWith(
            " -> DllNotFoundException: Unable to load DLL 'git2-example'.",
            line,
            StringComparison.Ordinal);
    }

    [Fact]
    public void An_exception_without_inners_is_a_single_segment()
    {
        var line = UnexpectedFailure.Describe(
            new InvalidOperationException("broker gone"),
            stackSwitch: null);

        Assert.Equal("InvalidOperationException: broker gone", line);
    }

    /// <summary>
    /// Locating #188 took the reporter a local rebuild whose only change was appending the
    /// stack. The switch exists so the next report does not need one — and stays off by
    /// default, because the one-line contract above is what operators and hooks parse.
    /// </summary>
    [Fact]
    public void The_stack_stays_hidden_without_the_switch()
    {
        var line = UnexpectedFailure.Describe(Thrown(), stackSwitch: null);

        Assert.DoesNotContain(Environment.NewLine, line, StringComparison.Ordinal);
    }

    [Fact]
    public void The_switch_appends_the_full_exception_under_the_line()
    {
        var line = UnexpectedFailure.Describe(Thrown(), stackSwitch: "1");

        Assert.StartsWith(
            "InvalidOperationException: boom" + Environment.NewLine,
            line,
            StringComparison.Ordinal);
        Assert.Contains(nameof(Thrown), line, StringComparison.Ordinal);
    }

    [Fact]
    public void The_switch_accepts_true_in_any_case()
    {
        var line = UnexpectedFailure.Describe(Thrown(), stackSwitch: "TRUE");

        Assert.Contains(Environment.NewLine, line, StringComparison.Ordinal);
    }

    [Fact]
    public void An_unrelated_value_leaves_the_stack_hidden()
    {
        var line = UnexpectedFailure.Describe(Thrown(), stackSwitch: "0");

        Assert.DoesNotContain(Environment.NewLine, line, StringComparison.Ordinal);
    }

    private static Exception Thrown()
    {
        try
        {
            throw new InvalidOperationException("boom");
        }
        catch (InvalidOperationException exception)
        {
            return exception;
        }
    }
}
