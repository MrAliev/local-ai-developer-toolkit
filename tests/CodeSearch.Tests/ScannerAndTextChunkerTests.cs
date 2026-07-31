using CodeSearch.Core.Chunking;
using CodeSearch.Core.Indexing;
using System.Diagnostics;

namespace CodeSearch.Tests;

public class GenericTextChunkerTests
{
    private readonly GenericTextChunker _chunker = new();

    [Fact]
    public void WindowsOverlapAndKeepExactLineNumbers()
    {
        var content = string.Join('\n', Enumerable.Range(1, 200).Select(i => $"line {i}"));
        var chunks = _chunker.Split("docs/guide.md", content).ToList();

        Assert.True(chunks.Count > 1);
        Assert.Equal(1, chunks[0].StartLine);
        Assert.True(chunks[1].StartLine < chunks[0].EndLine, "windows must overlap");
        Assert.Equal(200, chunks[^1].EndLine);
        Assert.All(chunks, c => Assert.Equal(ChunkKind.Text, c.Kind));
    }

    [Fact]
    public void ShortFileBecomesASingleChunk()
    {
        var chunks = _chunker.Split("a.sql", "select 1;").ToList();

        Assert.Single(chunks);
        Assert.Equal("a.sql", chunks[0].Symbol);
    }

    [Fact]
    public void BinaryContentIsSkipped()
    {
        Assert.Empty(_chunker.Split("blob.json", "abc\0def"));
    }
}

public class ChunkerFactoryTests
{
    [Theory]
    [InlineData("Services/OrderService.cs", true)]
    [InlineData("app/main.ts", true)]
    [InlineData("Migrations/001_init.sql", true)]
    [InlineData("README.md", true)]
    [InlineData("logo.png", false)]
    [InlineData("Order.Designer.cs", false)]
    [InlineData("Order.g.cs", false)]
    [InlineData("package-lock.json", false)]
    [InlineData("bundle.min.js", false)]
    public void KnowsWhatIsWorthIndexing(string path, bool indexable) =>
        Assert.Equal(indexable, ChunkerFactory.IsIndexable(path));

    [Fact]
    public void CSharpGoesToRoslynEverythingElseToTheGenericChunker()
    {
        Assert.IsType<RoslynChunker>(ChunkerFactory.Resolve("A.cs"));
        Assert.IsType<GenericTextChunker>(ChunkerFactory.Resolve("a.py"));
        Assert.Null(ChunkerFactory.Resolve("a.dll"));
    }
}

public class FileScannerTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"codesearch-scan-{Guid.NewGuid():N}");

    public FileScannerTests()
    {
        Write("Src/Good.cs");
        Write("Src/bin/Debug/Bad.cs");
        Write("Src/obj/Bad.cs");
        Write("node_modules/pkg/Bad.js");
        Write(".git/hooks/Bad.sh");
        Write(".claude/worktrees/branch-x/Src/Duplicate.cs");
        Write("Docs/Notes.md");
    }

    [Fact]
    public void SkipsBuildOutputVendorDirectoriesAndWorktrees()
    {
        var files = FileScanner.Enumerate(_root);

        Assert.Contains(Path.Combine("Src", "Good.cs"), files);
        Assert.Contains(Path.Combine("Docs", "Notes.md"), files);
        Assert.DoesNotContain(files, f => f.Contains("bin", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(files, f => f.Contains("obj", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(files, f => f.Contains("node_modules", StringComparison.OrdinalIgnoreCase));

        // A worktree is a full second checkout of the same repo: indexing it doubles every file
        // and fills results with near-identical hits from another branch.
        Assert.DoesNotContain(files, f => f.Contains("worktrees", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Skips_source_files_beneath_a_reparse_point()
    {
        var outsideRoot = Path.Combine(
            Path.GetTempPath(),
            "codesearch-scan-outside-" + Guid.NewGuid().ToString("N"));
        var linkPath = Path.Combine(_root, "Linked");
        Directory.CreateDirectory(outsideRoot);
        File.WriteAllText(
            Path.Combine(outsideRoot, "External.cs"),
            "class External {}\n");
        try
        {
            CreateDirectoryLink(linkPath, outsideRoot);

            var files = FileScanner.Enumerate(_root);

            Assert.DoesNotContain(
                files,
                path => path.Contains("External.cs", StringComparison.Ordinal));
        }
        finally
        {
            if (Directory.Exists(linkPath))
            {
                Directory.Delete(linkPath);
            }

            if (Directory.Exists(outsideRoot))
            {
                Directory.Delete(outsideRoot, recursive: true);
            }
        }
    }

    private void Write(string relPath)
    {
        var full = Path.Combine(_root, relPath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, "// content\n");
    }

    private static void CreateDirectoryLink(string linkPath, string targetPath)
    {
        if (!OperatingSystem.IsWindows())
        {
            Directory.CreateSymbolicLink(linkPath, targetPath);
            return;
        }

        var start = new ProcessStartInfo(
            Environment.GetEnvironmentVariable("ComSpec") ?? "cmd.exe")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        start.ArgumentList.Add("/d");
        start.ArgumentList.Add("/c");
        start.ArgumentList.Add("mklink");
        start.ArgumentList.Add("/J");
        start.ArgumentList.Add(linkPath);
        start.ArgumentList.Add(targetPath);
        using var process = Process.Start(start)!;
        process.WaitForExit();
        Assert.Equal(0, process.ExitCode);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
        }
    }
}

public sealed class GitAwareFileScannerTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "codesearch-git-scan-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public void Normalizes_git_paths_and_excludes_ignored_files()
    {
        Directory.CreateDirectory(Path.Combine(_root, "src"));
        Directory.CreateDirectory(Path.Combine(_root, "ignored"));
        File.WriteAllText(Path.Combine(_root, ".gitignore"), "ignored/\n");
        File.WriteAllText(Path.Combine(_root, "src", "Good.cs"), "class Good {}\n");
        File.WriteAllText(Path.Combine(_root, "ignored", "Secret.cs"), "class Secret {}\n");
        Git("init");
        Git("add", ".");

        var files = FileScanner.Enumerate(_root);

        Assert.Contains(Path.Combine("src", "Good.cs"), files);
        Assert.DoesNotContain(
            files,
            path => path.Contains("Secret.cs", StringComparison.OrdinalIgnoreCase));
    }

    private void Git(params string[] arguments)
    {
        var start = new ProcessStartInfo("git")
        {
            WorkingDirectory = _root,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        foreach (var argument in arguments)
        {
            start.ArgumentList.Add(argument);
        }

        using var process = Process.Start(start)!;
        process.WaitForExit();
        Assert.Equal(0, process.ExitCode);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            foreach (var path in Directory.EnumerateFileSystemEntries(
                         _root,
                         "*",
                         SearchOption.AllDirectories))
            {
                File.SetAttributes(path, FileAttributes.Normal);
            }

            Directory.Delete(_root, recursive: true);
        }
    }
}
