using CodeSearch.Core.Semantics;

namespace CodeSearch.Tests;

public class LanguageServerPolicyStoreTests
{
    [Fact]
    public void MissingConfigurationIsDisabledAndHasBoundedDefaults()
    {
        var store = new LanguageServerPolicyStore(TempRoot());

        var policy = store.Read();

        Assert.False(policy.Enabled);
        Assert.Equal(16 * 1024 * 1024, policy.MaximumMessageBytes);
        Assert.Contains("typescript", policy.Languages.Keys);
    }

    [Fact]
    public void RoundTripsConfiguredExecutablesAndLimits()
    {
        var root = TempRoot();
        var store = new LanguageServerPolicyStore(root);
        var policy = LanguageServerPolicy.Default with
        {
            Enabled = true,
            RequestTimeoutSeconds = 7,
            Languages = new Dictionary<string, LanguageServerAdapterPolicy>(StringComparer.Ordinal)
            {
                ["typescript"] = new(true, "custom-ts-server", ["--stdio", "--log-level=2"]),
            },
        };

        store.Write(policy);
        var loaded = store.Read();

        Assert.True(loaded.Enabled);
        Assert.Equal(7, loaded.RequestTimeoutSeconds);
        var spec = loaded.ProcessSpec("typescript");
        Assert.Equal("custom-ts-server", spec.Executable);
        Assert.Equal(["--stdio", "--log-level=2"], spec.Arguments);
    }

    [Fact]
    public void InvalidOrUnknownConfigurationFallsBackToDisabledDefaults()
    {
        var root = TempRoot();
        Directory.CreateDirectory(root);
        File.WriteAllText(
            Path.Combine(root, LanguageServerPolicy.FileName),
            "{\"schemaVersion\":1,\"enabled\":true,\"unknown\":true}");
        var store = new LanguageServerPolicyStore(root);

        var loaded = store.Read();

        Assert.False(loaded.Enabled);
        Assert.Throws<InvalidOperationException>(() => loaded.ProcessSpec("typescript"));
    }

    [Fact]
    public void DisabledLanguageCannotStartEvenWhenTheGlobalSwitchIsOn()
    {
        var policy = LanguageServerPolicy.Default with { Enabled = true };

        Assert.Throws<InvalidOperationException>(() => policy.ProcessSpec("python"));
    }

    private static string TempRoot() =>
        Path.Combine(Path.GetTempPath(), "language-server-policy-tests", Guid.NewGuid().ToString("N"));
}
