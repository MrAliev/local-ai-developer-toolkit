using System.Text;
using CodeSearch.Core.Semantics;

namespace CodeSearch.Tests;

public sealed class ScipAdapterRunnerTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "codesearch-scip-runner-" + Guid.NewGuid().ToString("N"));

    public ScipAdapterRunnerTests() => Directory.CreateDirectory(_root);

    [Fact]
    public async Task SkipsAnUnavailableIndexer()
    {
        var original = EmptyIndex();
        var result = await new ScipAdapterRunner().RunAsync(
            original,
            _root,
            new ScipAdapterSpec(
                "missing",
                "missing-scip-indexer-" + Guid.NewGuid().ToString("N"),
                []),
            TestContext.Current.CancellationToken);

        Assert.Equal(SemanticAdapterState.Skipped, result.Status.State);
        Assert.Same(original, result.Index);
        Assert.False(File.Exists(Path.Combine(_root, "index.scip")));
    }

    [Fact]
    public async Task PreservesAnExistingOutputArtifact()
    {
        var output = Path.Combine(_root, "index.scip");
        await File.WriteAllTextAsync(
            output,
            "belongs to the repository",
            TestContext.Current.CancellationToken);

        var result = await new ScipAdapterRunner().RunAsync(
            EmptyIndex(),
            _root,
            new ScipAdapterSpec("unused", "unused", []),
            TestContext.Current.CancellationToken);

        Assert.Equal(SemanticAdapterState.Skipped, result.Status.State);
        Assert.Equal(
            "belongs to the repository",
            await File.ReadAllTextAsync(output, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task RunsWindowsCommandShimsAndRemovesTheTemporaryArtifact()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var payload = MinimalIndex("demo.py", "x");
        var script = Path.Combine(_root, "fixture-indexer.cmd");
        await File.WriteAllTextAsync(
            script,
            "@powershell.exe -NoProfile -NonInteractive -Command \"" +
            "[IO.File]::WriteAllBytes('index.scip'," +
            "[Convert]::FromBase64String('" + Convert.ToBase64String(payload) + "'))\"\r\n",
            TestContext.Current.CancellationToken);
        var result = await new ScipAdapterRunner().RunAsync(
            EmptyIndex(),
            _root,
            new ScipAdapterSpec(
                "python",
                script,
                []),
            TestContext.Current.CancellationToken);

        Assert.Equal(SemanticAdapterState.Succeeded, result.Status.State);
        Assert.Equal("demo.py", Assert.Single(result.Index.Documents).RelPath);
        Assert.False(File.Exists(Path.Combine(_root, "index.scip")));
    }

    [Fact]
    public async Task RejectsExpandableCommandScriptArguments()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var script = Path.Combine(_root, "unsafe.cmd");
        await File.WriteAllTextAsync(
            script,
            "@exit /b 0\r\n",
            TestContext.Current.CancellationToken);

        var result = await new ScipAdapterRunner().RunAsync(
            EmptyIndex(),
            _root,
            new ScipAdapterSpec("unsafe", script, ["%PATH%"]),
            TestContext.Current.CancellationToken);

        Assert.Equal(SemanticAdapterState.Failed, result.Status.State);
        Assert.Contains("unsafe characters", result.Status.Message, StringComparison.Ordinal);
    }

    private static byte[] MinimalIndex(string path, string text)
    {
        var document = new List<byte>();
        String(document, 1, path);
        String(document, 5, text);
        Tag(document, 6, 0);
        Varint(document, 2);
        var index = new List<byte>();
        Tag(index, 2, 2);
        Varint(index, (ulong)document.Count);
        index.AddRange(document);
        return [.. index];
    }

    private static void String(List<byte> bytes, int field, string value)
    {
        var encoded = Encoding.UTF8.GetBytes(value);
        Tag(bytes, field, 2);
        Varint(bytes, (ulong)encoded.Length);
        bytes.AddRange(encoded);
    }

    private static void Tag(List<byte> bytes, int field, int wire) =>
        Varint(bytes, (ulong)((field << 3) | wire));

    private static void Varint(List<byte> bytes, ulong value)
    {
        while (value >= 0x80)
        {
            bytes.Add((byte)(value | 0x80));
            value >>= 7;
        }

        bytes.Add((byte)value);
    }

    private static SemanticIndex EmptyIndex() =>
        new()
        {
            RepositoryId = "repository",
            GenerationId = "generation",
            GitTree = "tree",
            BaseCommit = "commit",
            IndexedAtUtc = DateTime.UnixEpoch,
            Documents = [],
            Symbols = [],
            Occurrences = [],
            Relationships = [],
        };

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }
}
