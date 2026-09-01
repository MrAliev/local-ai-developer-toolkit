using CodeSearch.Core.Semantics;

namespace CodeSearch.Tests;

public sealed class SemanticIndexingPolicyStoreTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "semantic-policy-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public void UsesStrictDefaultsWhenTheFileIsMissingOrMalformed()
    {
        var store = new SemanticIndexingPolicyStore(_root);

        Assert.Equal(SemanticIndexingPolicy.Default, store.Read());
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(new SemanticIndexingPolicyStore(_root).Path)!);
        File.WriteAllText(store.Path, "{not-json");
        Assert.Equal(SemanticIndexingPolicy.Default, store.Read());
    }

    [Fact]
    public void PersistsAllRuntimeTuningWithoutARebuild()
    {
        var store = new SemanticIndexingPolicyStore(_root);
        var expected = SemanticIndexingPolicy.Default with
        {
            TimeoutSeconds = 17,
            MaximumProcessOutputBytes = 4096,
            TypeScript = SemanticIndexingPolicy.Default.TypeScript with
            {
                Enabled = false,
                Executable = "custom-ts",
                Arguments = ["make-index"],
                OutputFile = "custom.scip",
            },
        };

        store.Write(expected);
        var actual = store.Read();

        Assert.Equal(17, actual.TimeoutSeconds);
        Assert.Equal(4096, actual.MaximumProcessOutputBytes);
        Assert.False(actual.TypeScript.Enabled);
        Assert.Equal("custom-ts", actual.TypeScript.Executable);
        Assert.Equal(["make-index"], actual.TypeScript.Arguments);
        Assert.Equal("custom.scip", actual.TypeScript.OutputFile);
        Assert.DoesNotContain(
            Directory.EnumerateFiles(_root),
            path => path.EndsWith(".tmp", StringComparison.Ordinal));
    }

    [Fact]
    public void RejectsUnknownConfigurationFields()
    {
        var store = new SemanticIndexingPolicyStore(_root);
        store.Write(SemanticIndexingPolicy.Default);
        var json = File.ReadAllText(store.Path);
        File.WriteAllText(
            store.Path,
            json.TrimEnd().TrimEnd('}') + ",\"Unknown\":true}");

        Assert.Equal(SemanticIndexingPolicy.Default, store.Read());
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }
}
