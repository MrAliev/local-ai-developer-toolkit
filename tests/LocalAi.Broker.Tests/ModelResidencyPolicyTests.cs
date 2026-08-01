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
        store.Write(new BrokerPolicy(1, residency));

        Assert.Equal(residency, store.Read().ModelResidency);
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
