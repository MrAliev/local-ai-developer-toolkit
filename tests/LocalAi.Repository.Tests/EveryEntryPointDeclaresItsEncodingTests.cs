using System.IO;

namespace LocalAi.Repository.Tests;

/// <summary>
/// Every executable this toolkit ships says UTF-8 before it prints anything.
///
/// <c>ConsoleOutputTextTests</c> beside this one proves the decision is right; this proves it is
/// taken. They are different failures: the first is a wrong encoding, the second is a binary that
/// never asked — which is exactly what happened. The decision had been written out three times
/// and forgotten a fourth, and the one it was forgotten in was <c>localai.exe</c>, the one a
/// person types.
///
/// It reads the source because the alternative is starting five processes to ask them, and
/// because what went wrong was never a runtime condition: it was a line missing from a file.
/// </summary>
public sealed class EveryEntryPointDeclaresItsEncodingTests
{
    [Theory]
    [InlineData("LocalAi.Cli")]
    [InlineData("CodeSearch.Cli")]
    [InlineData("CodeSearch.Mcp")]
    [InlineData("LocalLm.Mcp")]
    [InlineData("LocalAi.ReleaseSigner")]
    public void The_entry_point_asks_for_Utf8(string project)
    {
        var path = Path.Combine(RepositoryRoot(), "src", project, "Program.cs");

        Assert.True(
            File.ReadAllText(path).Contains("ConsoleOutputText.UseUtf8(", StringComparison.Ordinal),
            $"src/{project}/Program.cs prints without asking for UTF-8. On Windows it will " +
            "write in the console's output code page, and under a Git hook or an MCP server " +
            "there is no console to have set one — so every character past ASCII is mangled, " +
            "including the em dash, the ellipsis and the emoji that opens a local-model notice.");
    }

    private static string RepositoryRoot()
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
