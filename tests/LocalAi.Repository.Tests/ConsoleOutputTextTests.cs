using System.Text;
using LocalAi.Contracts;

namespace LocalAi.Repository.Tests;

/// <summary>
/// Which encoding an entry point writes its own text in, and the fact that all of them have to
/// answer the question.
///
/// Three of them answered it in three separate copies of the same four lines, and a fourth —
/// <c>localai.exe</c>, the one a person actually types — never answered it at all, while
/// <see cref="ChildProcessText.Utf8"/> and the code that starts <c>localai sync</c> both said in
/// so many words that it did. That is the shape this pins: one place decides, and every entry
/// point whose text somebody reads goes through it.
/// </summary>
public sealed class ConsoleOutputTextTests
{
    /// <summary>
    /// Without a preamble, because the first three bytes of a process's output are not a
    /// property anybody wants: a caller reading the first line gets a byte-order mark glued to
    /// it, and a caller comparing output against an expected string fails on nothing.
    /// </summary>
    [Fact]
    public void Asks_for_utf8_and_not_for_a_byte_order_mark()
    {
        Encoding? asked = null;

        Assert.True(ConsoleOutputText.UseUtf8(encoding => asked = encoding));

        Assert.NotNull(asked);
        Assert.Equal(Encoding.UTF8.CodePage, asked!.CodePage);
        Assert.Empty(asked.GetPreamble());
    }

    /// <summary>
    /// A process with no console attached — under a Git hook, or as an MCP server started by a
    /// client — throws on the assignment. Its output is already UTF-8 there, so the answer is to
    /// carry on rather than to refuse to run: a tool that will not start because it could not
    /// state an encoding it already has is worse than the encoding question itself.
    /// </summary>
    [Fact]
    public void A_process_with_no_console_carries_on()
    {
        Assert.False(ConsoleOutputText.UseUtf8(_ => throw new IOException("no console")));
    }

    /// <summary>
    /// Anything else means the process is broken in a way an encoding helper must not swallow.
    /// </summary>
    [Fact]
    public void Any_other_failure_still_travels()
    {
        Assert.Throws<InvalidOperationException>(
            () => ConsoleOutputText.UseUtf8(_ => throw new InvalidOperationException("other")));
    }

    /// <summary>
    /// The list is the point. `localai.exe` was missing from it for as long as it has existed,
    /// and the drift was invisible because each of the other three carried its own copy of the
    /// code — there was no list to be missing from.
    ///
    /// Not here, and why: `LocalAi.Broker` writes no text to the console at all;
    /// `LocalAi.Launcher` copies its child's bytes from BaseStream to BaseStream, so it never
    /// decodes anything and has no encoding to declare.
    /// </summary>
    [Theory]
    [InlineData("src/LocalAi.Cli/Program.cs")]
    [InlineData("src/CodeSearch.Cli/Program.cs")]
    [InlineData("src/CodeSearch.Mcp/Program.cs")]
    [InlineData("src/LocalLm.Mcp/Program.cs")]
    [InlineData("src/LocalAi.ReleaseSigner/Program.cs")]
    public void Every_entry_point_that_prints_text_says_which_encoding_it_writes(string program)
    {
        var source = File.ReadAllText(Path.Combine(FindRepositoryRoot(), program));

        Assert.Contains("ConsoleOutputText.UseUtf8()", source, StringComparison.Ordinal);
    }

    /// <summary>
    /// One place decides, so a fifth entry point cannot quietly acquire a fourth opinion.
    /// </summary>
    [Theory]
    [InlineData("src/LocalAi.Cli/Program.cs")]
    [InlineData("src/CodeSearch.Cli/Program.cs")]
    [InlineData("src/CodeSearch.Mcp/Program.cs")]
    [InlineData("src/LocalLm.Mcp/Program.cs")]
    [InlineData("src/LocalAi.ReleaseSigner/Program.cs")]
    public void No_entry_point_keeps_its_own_copy(string program)
    {
        var source = File.ReadAllText(Path.Combine(FindRepositoryRoot(), program));

        Assert.DoesNotContain("Console.OutputEncoding", source, StringComparison.Ordinal);
    }

    private static string FindRepositoryRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory);
             directory is not null;
             directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "LocalAi.slnx")))
            {
                return directory.FullName;
            }
        }

        throw new DirectoryNotFoundException(
            $"Could not locate LocalAi.slnx from {AppContext.BaseDirectory}.");
    }
}
