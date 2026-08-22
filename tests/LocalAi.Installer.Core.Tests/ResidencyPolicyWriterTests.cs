using LocalAi.Contracts;
using LocalAi.Installer.Core.Activation;

namespace LocalAi.Installer.Core.Tests;

public sealed class ResidencyPolicyWriterTests : IDisposable
{
    private readonly string root = Path.Combine(
        Path.GetTempPath(),
        "LocalAi.Installer.Core.Residency.Tests",
        Guid.NewGuid().ToString("N"));

    /// <summary>
    /// The regression this class exists for. Writing the policy used to create the LocalAi
    /// root with a plain CreateDirectory, so a run that failed before installing anything left
    /// behind a directory with an inherited ACL — and the layout lease refuses such a root for
    /// good, which made every later installation on that machine fail with a message naming
    /// the condition and not the cure.
    /// </summary>
    [Fact]
    public void Does_not_create_the_installation_root()
    {
        var outcome = ResidencyPolicyWriter.Apply(root, ModelResidencyPolicy.AllowCpu);

        Assert.Equal(ResidencyPolicyOutcome.SkippedWithoutInstallation, outcome);
        Assert.False(Directory.Exists(root));
    }

    [Fact]
    public void Writes_the_policy_into_an_existing_installation()
    {
        Directory.CreateDirectory(root);

        var outcome = ResidencyPolicyWriter.Apply(
            root,
            ModelResidencyPolicy.AllowPartialOffload);

        Assert.Equal(ResidencyPolicyOutcome.Applied, outcome);
        Assert.Equal(
            ModelResidencyPolicy.AllowPartialOffload,
            new ModelResidencyPolicyStore(root).Read().ModelResidency);
    }

    /// <summary>
    /// Only the residency field is the wizard's to change. Everything else in the policy
    /// document belongs to whoever wrote it and has to survive an installation.
    /// </summary>
    [Fact]
    public void Keeps_the_rest_of_an_existing_policy()
    {
        Directory.CreateDirectory(root);
        var store = new ModelResidencyPolicyStore(root);
        var before = store.Read();
        store.Write(before with { ModelResidency = ModelResidencyPolicy.AllowCpu });

        ResidencyPolicyWriter.Apply(root, ModelResidencyPolicy.RequireFullVram);

        var after = store.Read();
        Assert.Equal(ModelResidencyPolicy.RequireFullVram, after.ModelResidency);
        Assert.Equal(
            before with { ModelResidency = ModelResidencyPolicy.RequireFullVram },
            after);
    }

    public void Dispose()
    {
        if (Directory.Exists(root))
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
