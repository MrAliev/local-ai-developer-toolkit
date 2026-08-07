using System.Security.Cryptography;
using System.Text;
using LocalAi.Installer.Core.Dependencies;

namespace LocalAi.Installer.Core.Tests;

public sealed class ScipPythonWindowsCompatibilityPatchTests : IDisposable
{
    private const string OriginalExpression = "new RegExp(o.sep,\"g\")";
    private const string PatchedExpression =
        "new RegExp(o.sep.replace(\"\\\\\",\"\\\\\\\\\"),\"g\")";
    private const string OriginalPythonSelection =
        "if(void 0===f)if((0,m.sync)(\"python3\"))f=\"python3\";else{" +
        "if(!(0,m.sync)(\"python\"))";
    private const string PatchedPythonSelection =
        "if(void 0===f)if(\"win32\"===process.platform&&(0,m.sync)(\"python\"))" +
        "f=\"python\";else if((0,m.sync)(\"python3\"))f=\"python3\";else{" +
        "if(!(0,m.sync)(\"python\"))";

    private readonly string root = Path.Combine(
        Path.GetTempPath(),
        $"localai-scip-python-patch-{Guid.NewGuid():N}");

    [Fact]
    public void Applies_verified_patch_atomically_and_is_idempotent()
    {
        const string original =
            "before;" + OriginalExpression + ";" + OriginalPythonSelection + ";after";
        var patched = original.Replace(
            OriginalExpression,
            PatchedExpression,
            StringComparison.Ordinal).Replace(
            OriginalPythonSelection,
            PatchedPythonSelection,
            StringComparison.Ordinal);
        var fixture = CreateFixture(original);
        var sut = CreatePatch(original, patched);

        var first = sut.Apply(root, ScipPythonWindowsCompatibilityPatch.SupportedVersion);
        var second = sut.Apply(root, ScipPythonWindowsCompatibilityPatch.SupportedVersion);

        Assert.Equal(ScipPythonPatchOutcome.Applied, first);
        Assert.Equal(ScipPythonPatchOutcome.AlreadyApplied, second);
        Assert.Equal(patched, File.ReadAllText(fixture.BundlePath));
        Assert.Empty(Directory.GetFiles(
            Path.GetDirectoryName(fixture.BundlePath)!,
            "*.tmp"));
    }

    [Fact]
    public void Refuses_an_unverified_bundle_without_changing_it()
    {
        const string original =
            "before;" + OriginalExpression + ";" + OriginalPythonSelection + ";after";
        var fixture = CreateFixture(original + ";unexpected");
        var sut = CreatePatch(
            original,
            original.Replace(
                OriginalExpression,
                PatchedExpression,
                StringComparison.Ordinal).Replace(
                OriginalPythonSelection,
                PatchedPythonSelection,
                StringComparison.Ordinal));

        var exception = Assert.Throws<InvalidOperationException>(() =>
            sut.Apply(root, ScipPythonWindowsCompatibilityPatch.SupportedVersion));

        Assert.Contains("refusing to modify", exception.Message);
        Assert.Equal(original + ";unexpected", File.ReadAllText(fixture.BundlePath));
    }

    [Fact]
    public void Refuses_a_verified_bundle_missing_an_expected_expression()
    {
        const string original = "before;" + OriginalExpression + ";after";
        var fixture = CreateFixture(original);
        var sut = new ScipPythonWindowsCompatibilityPatch(
            Hash(original),
            Hash("different"),
            OriginalExpression,
            PatchedExpression,
            OriginalPythonSelection,
            PatchedPythonSelection);

        var exception = Assert.Throws<InvalidOperationException>(() =>
            sut.Apply(root, ScipPythonWindowsCompatibilityPatch.SupportedVersion));

        Assert.Contains("each expected", exception.Message);
        Assert.Equal(original, File.ReadAllText(fixture.BundlePath));
    }

    [Fact]
    public void Refuses_a_different_installed_package_version()
    {
        const string original =
            "before;" + OriginalExpression + ";" + OriginalPythonSelection + ";after";
        var fixture = CreateFixture(original, installedVersion: "0.6.5");
        var sut = CreatePatch(
            original,
            original.Replace(
                OriginalExpression,
                PatchedExpression,
                StringComparison.Ordinal).Replace(
                OriginalPythonSelection,
                PatchedPythonSelection,
                StringComparison.Ordinal));

        var exception = Assert.Throws<InvalidOperationException>(() =>
            sut.Apply(root, ScipPythonWindowsCompatibilityPatch.SupportedVersion));

        Assert.Contains("npm installed '0.6.5'", exception.Message);
        Assert.Equal(original, File.ReadAllText(fixture.BundlePath));
    }

    [Fact]
    public void Refuses_to_patch_an_unlisted_requested_version()
    {
        const string original =
            "before;" + OriginalExpression + ";" + OriginalPythonSelection + ";after";
        _ = CreateFixture(original);
        var sut = CreatePatch(
            original,
            original.Replace(
                OriginalExpression,
                PatchedExpression,
                StringComparison.Ordinal).Replace(
                OriginalPythonSelection,
                PatchedPythonSelection,
                StringComparison.Ordinal));

        var exception = Assert.Throws<InvalidOperationException>(() =>
            sut.Apply(root, "0.6.7"));

        Assert.Contains("supports version 0.6.6", exception.Message);
    }

    public void Dispose()
    {
        if (Directory.Exists(root))
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private (string BundlePath, string ManifestPath) CreateFixture(
        string bundle,
        string installedVersion = ScipPythonWindowsCompatibilityPatch.SupportedVersion)
    {
        var packageRoot = Path.Combine(root, "@sourcegraph", "scip-python");
        var dist = Path.Combine(packageRoot, "dist");
        Directory.CreateDirectory(dist);
        var bundlePath = Path.Combine(dist, "scip-python.js");
        var manifestPath = Path.Combine(packageRoot, "package.json");
        File.WriteAllText(bundlePath, bundle, new UTF8Encoding(false));
        File.WriteAllText(
            manifestPath,
            $"{{\"version\":\"{installedVersion}\"}}",
            new UTF8Encoding(false));
        return (bundlePath, manifestPath);
    }

    private static ScipPythonWindowsCompatibilityPatch CreatePatch(
        string original,
        string patched) => new(
            Hash(original),
            Hash(patched),
            OriginalExpression,
            PatchedExpression,
            OriginalPythonSelection,
            PatchedPythonSelection);

    private static string Hash(string value) => Convert.ToHexString(
        SHA256.HashData(new UTF8Encoding(false).GetBytes(value)));
}
