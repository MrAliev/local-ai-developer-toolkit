using LocalAi.Contracts;

namespace LocalAi.Broker.Tests;

public sealed class ModelResidencyPolicyTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "LocalAi.ResidencyPolicyTests",
        Guid.NewGuid().ToString("N"));

    public ModelResidencyPolicyTests() => Directory.CreateDirectory(_root);

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

    [Fact]
    public void Missing_policy_file_is_strict()
    {
        var policy = new ModelResidencyPolicyStore(_root).Read();

        Assert.Equal(ModelResidencyPolicy.RequireFullVram, policy.ModelResidency);
        Assert.Equal(0, policy.IdleModelKeepAliveSeconds);
    }

    [Fact]
    public void Malformed_policy_file_does_not_silently_relax_the_check()
    {
        File.WriteAllText(Path.Combine(_root, BrokerPolicy.FileName), "{ not json");

        var policy = new ModelResidencyPolicyStore(_root).Read();

        Assert.Equal(ModelResidencyPolicy.RequireFullVram, policy.ModelResidency);
    }

    [Fact]
    public void Unknown_policy_value_falls_back_to_strict()
    {
        File.WriteAllText(
            Path.Combine(_root, BrokerPolicy.FileName),
            """{"SchemaVersion":1,"ModelResidency":"AllowAnythingPlease"}""");

        var policy = new ModelResidencyPolicyStore(_root).Read();

        Assert.Equal(ModelResidencyPolicy.RequireFullVram, policy.ModelResidency);
    }

    [Fact]
    public void Future_schema_version_falls_back_to_strict()
    {
        File.WriteAllText(
            Path.Combine(_root, BrokerPolicy.FileName),
            """{"SchemaVersion":99,"ModelResidency":"AllowCpu"}""");

        var policy = new ModelResidencyPolicyStore(_root).Read();

        Assert.Equal(ModelResidencyPolicy.RequireFullVram, policy.ModelResidency);
    }

    [Theory]
    [InlineData(ModelResidencyPolicy.AllowPartialOffload)]
    [InlineData(ModelResidencyPolicy.AllowCpu)]
    public void Written_policy_round_trips(ModelResidencyPolicy residency)
    {
        var store = new ModelResidencyPolicyStore(_root);
        store.Write(new BrokerPolicy(1, residency, IdleModelKeepAliveSeconds: 45));

        Assert.Equal(residency, store.Read().ModelResidency);
        Assert.Equal(45, store.Read().IdleModelKeepAliveSeconds);
    }

    [Fact]
    public void Existing_policy_without_idle_keep_alive_uses_immediate_unload()
    {
        File.WriteAllText(
            Path.Combine(_root, BrokerPolicy.FileName),
            """{"SchemaVersion":1,"ModelResidency":"RequireFullVram"}""");

        var policy = new ModelResidencyPolicyStore(_root).Read();

        Assert.Equal(0, policy.IdleModelKeepAliveSeconds);
    }

    /// <summary>
    /// A file that stopped parsing is not the same as no file, and the store is the only thing
    /// that knows which it read. It answers a malformed document with its defaults — right at
    /// runtime, and invisible to whoever wrote the file: it is still on disk, still looks
    /// configured, and no longer does anything.
    /// </summary>
    [Fact]
    public void A_file_that_did_not_parse_is_reported_as_found_and_unused()
    {
        File.WriteAllText(
            Path.Combine(_root, BrokerPolicy.FileName),
            "{ this is not json");

        var read = new ModelResidencyPolicyStore(_root).ReadWithSource();

        Assert.True(read.FileFound);
        Assert.False(read.FileUsed);
        Assert.Equal(BrokerPolicy.Default, read.Policy);
    }

    [Fact]
    public void A_file_the_store_could_use_is_reported_as_used()
    {
        new ModelResidencyPolicyStore(_root).Write(
            BrokerPolicy.Default with { IdleModelKeepAliveSeconds = 45 });

        var read = new ModelResidencyPolicyStore(_root).ReadWithSource();

        Assert.True(read.FileFound);
        Assert.True(read.FileUsed);
        Assert.Equal(45, read.Policy.IdleModelKeepAliveSeconds);
    }

    [Fact]
    public void No_file_at_all_is_neither_found_nor_used()
    {
        var read = new ModelResidencyPolicyStore(_root).ReadWithSource();

        Assert.False(read.FileFound);
        Assert.False(read.FileUsed);
    }

    /// <summary>
    /// A schema this build does not know is refused the same way malformed JSON is: the values
    /// are not applied, so the file is found and unused rather than found and honoured.
    /// </summary>
    [Fact]
    public void A_retention_file_from_another_schema_is_found_and_unused()
    {
        Directory.CreateDirectory(Path.Combine(_root, "settings"));
        File.WriteAllText(
            Path.Combine(_root, "settings", RuntimeRetentionPolicy.FileName),
            """{"SchemaVersion":99,"GenerationsPerRepository":7}""");

        var read = new RuntimeRetentionPolicyStore(_root).ReadWithSource();

        Assert.True(read.FileFound);
        Assert.False(read.FileUsed);
        Assert.Equal(
            RuntimeRetentionPolicy.Default.GenerationsPerRepository,
            read.Policy.GenerationsPerRepository);
    }

    [Fact]
    public void Negative_idle_keep_alive_falls_back_to_default_policy()
    {
        File.WriteAllText(
            Path.Combine(_root, BrokerPolicy.FileName),
            """{"SchemaVersion":1,"ModelResidency":"AllowCpu","IdleModelKeepAliveSeconds":-1}""");

        var policy = new ModelResidencyPolicyStore(_root).Read();

        Assert.Equal(BrokerPolicy.Default, policy);
    }

    [Fact]
    public void Fully_resident_load_carries_no_warning()
    {
        Assert.Null(
            ModelResidencyPolicy.AllowCpu.DescribeDegradation(
                sizeBytes: 1000,
                sizeVramBytes: 1000));
    }

    [Fact]
    public void Cpu_load_is_reported_as_running_on_cpu()
    {
        var warning = ModelResidencyPolicy.AllowCpu.DescribeDegradation(
            sizeBytes: 1000,
            sizeVramBytes: 0);

        Assert.NotNull(warning);
        Assert.Contains("CPU", warning, StringComparison.Ordinal);
    }

    [Fact]
    public void Partial_load_reports_the_resident_share()
    {
        var warning = ModelResidencyPolicy.AllowPartialOffload.DescribeDegradation(
            sizeBytes: 1000,
            sizeVramBytes: 400);

        Assert.NotNull(warning);
        Assert.Contains("40%", warning, StringComparison.Ordinal);
    }
}
