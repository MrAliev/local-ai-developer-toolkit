using System.Text.Json;
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

    /// <summary>
    /// Built from a real file rather than hand-written, and that is the whole point.
    ///
    /// This store names its members the way the CLR does, because no naming policy is
    /// configured. The hand-written camelCase document this test used to carry was rejected
    /// because <em>every</em> member was unmapped, so it passed whether or not the unknown-member
    /// guard existed at all — a test that could not fail is worse than no test, because the
    /// coverage report counts it.
    /// </summary>
    [Fact]
    public void AnUnknownMemberFallsBackToDisabledDefaults()
    {
        var root = TempRoot();
        var store = new LanguageServerPolicyStore(root);
        store.Write(LanguageServerPolicy.Default with { Enabled = true });
        File.WriteAllText(
            store.Path,
            File.ReadAllText(store.Path).TrimEnd().TrimEnd('}') + ",\"Unknown\":true}");

        var loaded = store.Read();

        Assert.False(loaded.Enabled);
        Assert.Throws<InvalidOperationException>(() => loaded.ProcessSpec("typescript"));
    }

    /// <summary>
    /// The other half of what the previous single test claimed to cover: a document that binds
    /// cleanly and carries a value the policy will not accept.
    /// </summary>
    [Fact]
    public void AnOutOfRangeValueFallsBackToDisabledDefaults()
    {
        var root = TempRoot();
        Directory.CreateDirectory(root);
        var store = new LanguageServerPolicyStore(root);
        File.WriteAllBytes(
            store.Path,
            JsonSerializer.SerializeToUtf8Bytes(
                LanguageServerPolicy.Default with
                {
                    Enabled = true,
                    RequestTimeoutSeconds = 0,
                }));

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

    [Fact]
    public void InitializationOptionsSurviveTheFileAndReachTheProcessSpec()
    {
        var root = TempRoot();
        var store = new LanguageServerPolicyStore(root);
        store.Write(LanguageServerPolicy.Default with
        {
            Enabled = true,
            Languages = new Dictionary<string, LanguageServerAdapterPolicy>(StringComparer.Ordinal)
            {
                ["typescript"] = new(
                    true,
                    "typescript-language-server",
                    ["--stdio"],
                    JsonSerializer.SerializeToElement(
                        new { tsserver = new { path = "/opt/typescript/lib/tsserver.js" } })),
            },
        });

        var options = store.Read().ProcessSpec("typescript").InitializationOptions;

        Assert.NotNull(options);
        Assert.Equal(
            "/opt/typescript/lib/tsserver.js",
            options!.Value.GetProperty("tsserver").GetProperty("path").GetString());
    }

    /// <summary>
    /// An installation configured before this field existed has to keep working. The field is
    /// optional rather than a schema bump for exactly that reason: bumping the version would
    /// invalidate every existing file and silently replace it with the disabled defaults.
    /// </summary>
    [Fact]
    public void ConfigurationWrittenBeforeTheFieldExistedStillLoads()
    {
        var root = TempRoot();
        Directory.CreateDirectory(root);
        File.WriteAllText(
            Path.Combine(root, LanguageServerPolicy.FileName),
            """
            {
              "SchemaVersion": 1,
              "Enabled": true,
              "RequestTimeoutSeconds": 15,
              "ShutdownTimeoutSeconds": 3,
              "MaximumMessageBytes": 16777216,
              "MaximumStandardErrorBytes": 1048576,
              "Languages": {
                "typescript": {
                  "Enabled": true,
                  "Executable": "typescript-language-server",
                  "Arguments": ["--stdio"]
                }
              }
            }
            """);

        var spec = new LanguageServerPolicyStore(root).Read().ProcessSpec("typescript");

        Assert.Equal("typescript-language-server", spec.Executable);
        Assert.Null(spec.InitializationOptions);
    }

    [Theory]
    [InlineData("\"a string\"")]
    [InlineData("[1, 2, 3]")]
    [InlineData("42")]
    public void NonObjectInitializationOptionsAreRejected(string json)
    {
        var policy = PolicyWithOptions(JsonSerializer.Deserialize<JsonElement>(json));

        // Rejected at the door rather than sent on. A server handed a string where it expects an
        // object fails at initialize, and the message it produces is about its own internals.
        Assert.Throws<ArgumentException>(
            () => new LanguageServerPolicyStore(TempRoot()).Write(policy));
    }

    [Fact]
    public void OversizedInitializationOptionsAreRejected()
    {
        var policy = PolicyWithOptions(JsonSerializer.SerializeToElement(
            new { blob = new string('x', 64 * 1024) }));

        Assert.Throws<ArgumentException>(
            () => new LanguageServerPolicyStore(TempRoot()).Write(policy));
    }

    [Fact]
    public void AFileCarryingUnusableInitializationOptionsFallsBackToDisabledDefaults()
    {
        var root = TempRoot();
        Directory.CreateDirectory(root);
        File.WriteAllText(
            Path.Combine(root, LanguageServerPolicy.FileName),
            """
            {
              "SchemaVersion": 1,
              "Enabled": true,
              "RequestTimeoutSeconds": 15,
              "ShutdownTimeoutSeconds": 3,
              "MaximumMessageBytes": 16777216,
              "MaximumStandardErrorBytes": 1048576,
              "Languages": {
                "typescript": {
                  "Enabled": true,
                  "Executable": "typescript-language-server",
                  "Arguments": ["--stdio"],
                  "InitializationOptions": "not-an-object"
                }
              }
            }
            """);

        var loaded = new LanguageServerPolicyStore(root).Read();

        Assert.False(loaded.Enabled);
    }

    private static LanguageServerPolicy PolicyWithOptions(JsonElement options) =>
        LanguageServerPolicy.Default with
        {
            Enabled = true,
            Languages = new Dictionary<string, LanguageServerAdapterPolicy>(StringComparer.Ordinal)
            {
                ["typescript"] = new(true, "typescript-language-server", ["--stdio"], options),
            },
        };

    private static string TempRoot() =>
        Path.Combine(Path.GetTempPath(), "language-server-policy-tests", Guid.NewGuid().ToString("N"));
}
