using LocalLm.Core;

namespace LocalLm.Tests;

public sealed class LogTriagePolicyTests
{
    [Fact]
    public void Missing_or_malformed_policy_uses_defaults()
    {
        var runtimeRoot = TemporaryRoot();
        try
        {
            var store = new LogTriagePolicyStore(runtimeRoot);

            Assert.Equal(LogTriagePolicy.Default, store.Read());
            Directory.CreateDirectory(runtimeRoot);
            File.WriteAllText(store.Path, "{ not json");
            Assert.Equal(LogTriagePolicy.Default, store.Read());
            File.WriteAllText(store.Path, "{\"schemaVersion\": 99}");
            Assert.Equal(LogTriagePolicy.Default, store.Read());
        }
        finally
        {
            Delete(runtimeRoot);
        }
    }

    [Fact]
    public void Write_round_trips_a_normalized_hand_editable_policy()
    {
        var runtimeRoot = TemporaryRoot();
        try
        {
            var store = new LogTriagePolicyStore(runtimeRoot);
            var requested = LogTriagePolicy.Default with
            {
                MaximumContextTokens = 16_384,
                ReservedContextTokens = 2_048,
                CharactersPerToken = 1.5,
                MaximumFragmentCharacters = 12_345,
                MaximumOverlapCharacters = 777,
                MaximumPartialSummaryCharacters = 1_500,
                PromptOverheadCharacters = 900,
            };

            store.Write(requested);

            Assert.Equal(requested.Normalized(), store.Read());
            Assert.Equal(
                Path.Combine(runtimeRoot, LogTriagePolicy.FileName),
                store.Path);
            Assert.Contains(
                "\"maximumContextTokens\"",
                File.ReadAllText(store.Path),
                StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            Delete(runtimeRoot);
        }
    }

    private static string TemporaryRoot() => Path.Combine(
        Path.GetTempPath(),
        $"localai-log-policy-{Guid.NewGuid():N}");

    private static void Delete(string path)
    {
        if (Directory.Exists(path))
        {
            Directory.Delete(path, recursive: true);
        }
    }
}
