using System.Text.Json;
using LocalAi.Contracts;
using LocalAi.Installer.Core.Models;
using LocalAi.Installer.Core.Releases;
using LocalAi.ReleaseSigner;

namespace LocalAi.ReleaseSigner.Tests;

/// <summary>
/// The list a release is signed with, and the reason it exists at all: <c>--models</c> was
/// optional, nothing passed it, and sixteen releases went out with an empty list. Their
/// installers offered six models on the models page and installed none of them.
/// </summary>
public sealed class ReleaseModelListTests
{
    [Fact]
    public async Task Names_every_catalogue_model_at_every_context_it_permits()
    {
        var catalogue = ModelRoutingCatalogResource.SelectableModels();
        var sizes = new FixedSizeSource(2_000_000_000);

        var entries = await ReleaseModelList.BuildAsync(
            sizes,
            TestContext.Current.CancellationToken);

        Assert.Equal(
            catalogue.Sum(model => model.ContextTokens.Distinct().Count()),
            entries.Count);
        Assert.Equal(
            catalogue.Select(model => model.Tag).OrderBy(tag => tag, StringComparer.Ordinal),
            entries.Select(entry => entry.Name).Distinct().OrderBy(
                tag => tag,
                StringComparer.Ordinal));
        // The recommendation engine adds the runtime and per-token reserves itself, so the
        // download size is the base weight and inflating it here would double count.
        Assert.All(
            entries,
            entry => Assert.Equal(entry.DownloadSize, entry.EstimatedVramBytes));
    }

    /// <summary>
    /// A size that cannot be read has to stop the release. Skipping the model would publish a
    /// manifest that silently cannot install it — the same silent omission this file ends.
    /// </summary>
    [Fact]
    public async Task Refuses_to_sign_a_model_whose_size_is_unknown()
    {
        var missing = ModelRoutingCatalogResource.SelectableModels()[0].Tag;
        var sizes = new FixedSizeSource(2_000_000_000, unknownTag: missing);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => ReleaseModelList.BuildAsync(sizes, TestContext.Current.CancellationToken));

        Assert.Contains(missing, exception.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// What <c>sign</c> reads back has to be what <c>models</c> wrote, so the two halves are
    /// exercised together rather than trusted to agree.
    /// </summary>
    [Fact]
    public async Task Renders_a_document_the_signer_reads_back_unchanged()
    {
        var entries = await ReleaseModelList.BuildAsync(
            new FixedSizeSource(1_234_567_890),
            TestContext.Current.CancellationToken);

        using var document = JsonDocument.Parse(ReleaseModelList.Render(entries));
        var parsed = document.RootElement.EnumerateArray()
            .Select(element => new ReleaseModelList.Entry(
                element.GetProperty("Name").GetString()!,
                element.GetProperty("ContextTokens").GetInt32(),
                element.GetProperty("DownloadSize").GetInt64(),
                element.GetProperty("EstimatedVramBytes").GetInt64()))
            .ToArray();

        Assert.Equal(entries, parsed);
    }

    /// <summary>
    /// Every context the catalogue declares must be one a manifest accepts: a power of two
    /// between 2048 and 262144. A release that fails this fails here, where the message names
    /// the model, rather than inside the verifier as "invalid manifest".
    /// </summary>
    [Fact]
    public async Task Every_catalogue_context_fits_what_a_manifest_accepts()
    {
        var entries = await ReleaseModelList.BuildAsync(
            new FixedSizeSource(1024),
            TestContext.Current.CancellationToken);

        Assert.All(
            entries,
            entry =>
            {
                Assert.InRange(entry.ContextTokens, 2048, 262144);
                Assert.Equal(0, entry.ContextTokens & (entry.ContextTokens - 1));
            });
        Assert.InRange(entries.Count, 1, 128);
    }

    /// <summary>
    /// The list has to survive the canonical writer the installer verifies against, which
    /// enforces its own rules about names, contexts, sizes and per-model consistency. Proving
    /// that here means a release fails in the signer, on the build machine, rather than as
    /// "invalid manifest" on somebody else's.
    /// </summary>
    [Fact]
    public async Task Produces_a_manifest_the_verifier_accepts()
    {
        var entries = await ReleaseModelList.BuildAsync(
            new FixedSizeSource(9_000_000_000),
            TestContext.Current.CancellationToken);

        var manifest = new ReleaseManifest(
            schemaVersion: 1,
            releaseVersion: "0.1.45",
            versionDirectory: "0123456789ab",
            modelCatalogVersion: "1",
            protocolVersion: 1,
            buildCompatibilityId: "localai-broker-v1",
            packageUri: ReleaseConsistency.ExpectedPackageUri(ReleaseVersion.Parse("0.1.45")),
            packageSize: 235_320_088,
            packageSha256: new string('A', 64),
            requiresAuthenticode: false,
            models: [.. entries.Select(entry => new ManifestModel(
                entry.Name,
                entry.ContextTokens,
                entry.DownloadSize,
                entry.EstimatedVramBytes))]);

        var payload = ReleaseManifestVerifier.CreateCanonicalUnsignedPayload(manifest);

        Assert.NotEmpty(payload);
        Assert.Empty(ReleaseConsistency.Check(
            manifest,
            ReleaseVersion.Parse("0.1.45"),
            "0123456789abcdef0123456789abcdef01234567"));
    }

    private sealed class FixedSizeSource(long size, string? unknownTag = null) : IModelSizeSource
    {
        public Task<long?> GetDownloadSizeBytesAsync(
            string tag,
            CancellationToken cancellationToken) =>
            Task.FromResult<long?>(
                string.Equals(tag, unknownTag, StringComparison.Ordinal) ? null : size);
    }
}
