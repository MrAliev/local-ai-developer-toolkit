using CodeSearch.Core.Indexing;

namespace CodeSearch.Tests;

public sealed class DirtyCorpusPolicyTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "localai-dirty-" + Guid.NewGuid().ToString("N"));

    [Theory]
    [InlineData("src/App.cs", false, true)]
    [InlineData("src/new.ts", false, true)]
    [InlineData("src/App.csproj", false, true)]
    [InlineData("src/View.razor", false, true)]
    [InlineData("src/App.cs", true, false)]
    [InlineData(".env", false, false)]
    [InlineData("config/credentials.json", false, false)]
    [InlineData("certs/client.pfx", false, false)]
    [InlineData("bin/generated.cs", false, false)]
    [InlineData("image.png", false, false)]
    public void Applies_source_ignore_secret_and_generated_rules(
        string path,
        bool ignored,
        bool expected)
    {
        Assert.Equal(expected, DirtyCorpusPolicy.IsAllowed(path, ignored));
    }

    [Fact]
    public void Relevant_content_change_invalidates_hash()
    {
        Directory.CreateDirectory(_root);
        File.WriteAllText(Path.Combine(_root, "App.cs"), "one");
        var first = DirtyCorpusPolicy.ComputeContentHash(_root, ["App.cs"]);
        File.WriteAllText(Path.Combine(_root, "App.cs"), "two");
        var second = DirtyCorpusPolicy.ComputeContentHash(_root, ["App.cs"]);

        Assert.NotEqual(first, second);
    }

    [Fact]
    public void Line_ending_styles_have_one_canonical_content_hash()
    {
        Directory.CreateDirectory(_root);
        var path = Path.Combine(_root, "App.cs");
        var hashes = new[]
            {
                "one\ntwo\n",
                "one\r\ntwo\r\n",
                "one\rtwo\r",
                "one\r\ntwo\n"
            }
            .Select(content =>
            {
                File.WriteAllText(path, content);
                return DirtyCorpusPolicy.ComputeContentHash(_root, ["App.cs"]);
            })
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        Assert.Single(hashes);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }
}
