using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace LocalAi.Installer.Core.Dependencies;

public enum ScipPythonPatchOutcome
{
    Applied,
    AlreadyApplied,
}

/// <summary>
/// Repairs two defects in the published 0.6.6 Windows bundle: it constructs a regular
/// expression directly from <c>path.sep</c>, and it prefers the Microsoft Store
/// <c>python3.exe</c> alias over an installed <c>python.exe</c>. The exact package and
/// resulting bytes are both pinned by SHA-256.
/// </summary>
public sealed class ScipPythonWindowsCompatibilityPatch
{
    public const string SupportedVersion = "0.6.6";

    private const string PublishedBundleSha256 =
        "55C645ED91E34EA4A2A7B47B1C482162CF173882E335A64C48EA6E8CBDB0EC05";
    private const string PatchedBundleSha256 =
        "88D9829BEAAC549F1D3BC80D48AF8CAB6985AB61F115A6C90BC5E289C9670B83";
    private const string PublishedExpression = "new RegExp(o.sep,\"g\")";
    private const string PatchedExpression =
        "new RegExp(o.sep.replace(\"\\\\\",\"\\\\\\\\\"),\"g\")";
    private const string PublishedPythonSelection =
        "if(void 0===f)if((0,m.sync)(\"python3\"))f=\"python3\";else{" +
        "if(!(0,m.sync)(\"python\"))";
    private const string PatchedPythonSelection =
        "if(void 0===f)if(\"win32\"===process.platform&&(0,m.sync)(\"python\"))" +
        "f=\"python\";else if((0,m.sync)(\"python3\"))f=\"python3\";else{" +
        "if(!(0,m.sync)(\"python\"))";

    private readonly string publishedBundleSha256;
    private readonly string patchedBundleSha256;
    private readonly string publishedExpression;
    private readonly string patchedExpression;
    private readonly string publishedPythonSelection;
    private readonly string patchedPythonSelection;

    public ScipPythonWindowsCompatibilityPatch()
        : this(
            PublishedBundleSha256,
            PatchedBundleSha256,
            PublishedExpression,
            PatchedExpression,
            PublishedPythonSelection,
            PatchedPythonSelection)
    {
    }

    internal ScipPythonWindowsCompatibilityPatch(
        string publishedBundleSha256,
        string patchedBundleSha256,
        string publishedExpression,
        string patchedExpression,
        string publishedPythonSelection,
        string patchedPythonSelection)
    {
        this.publishedBundleSha256 = publishedBundleSha256;
        this.patchedBundleSha256 = patchedBundleSha256;
        this.publishedExpression = publishedExpression;
        this.patchedExpression = patchedExpression;
        this.publishedPythonSelection = publishedPythonSelection;
        this.patchedPythonSelection = patchedPythonSelection;
    }

    public ScipPythonPatchOutcome Apply(string npmGlobalRoot, string packageVersion)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(npmGlobalRoot);
        ArgumentException.ThrowIfNullOrWhiteSpace(packageVersion);
        if (!string.Equals(packageVersion, SupportedVersion, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"SCIP Python compatibility patch supports version {SupportedVersion}, " +
                $"not {packageVersion}.");
        }

        var packageRoot = Path.GetFullPath(
            Path.Combine(npmGlobalRoot, "@sourcegraph", "scip-python"));
        VerifyPackageVersion(packageRoot, packageVersion);

        var bundlePath = Path.Combine(packageRoot, "dist", "scip-python.js");
        if (!File.Exists(bundlePath))
        {
            throw new InvalidOperationException(
                $"The SCIP Python bundle was not found at '{bundlePath}'.");
        }

        var currentHash = ComputeSha256(bundlePath);
        if (string.Equals(currentHash, patchedBundleSha256, StringComparison.Ordinal))
        {
            return ScipPythonPatchOutcome.AlreadyApplied;
        }

        if (!string.Equals(currentHash, publishedBundleSha256, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "The installed SCIP Python bundle does not match the verified 0.6.6 " +
                $"artifact (SHA-256 {currentHash}); refusing to modify it.");
        }

        var source = File.ReadAllText(bundlePath);
        if (CountOccurrences(source, publishedExpression) != 1 ||
            CountOccurrences(source, publishedPythonSelection) != 1)
        {
            throw new InvalidOperationException(
                "The verified SCIP Python bundle did not contain exactly one copy of " +
                "each expected Windows-incompatible expression.");
        }

        var patched = source.Replace(
            publishedExpression,
            patchedExpression,
            StringComparison.Ordinal).Replace(
            publishedPythonSelection,
            patchedPythonSelection,
            StringComparison.Ordinal);
        var temporaryPath = bundlePath + $".localai-{Guid.NewGuid():N}.tmp";
        try
        {
            File.WriteAllText(temporaryPath, patched, new UTF8Encoding(false));
            var stagedHash = ComputeSha256(temporaryPath);
            if (!string.Equals(stagedHash, patchedBundleSha256, StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    "The staged SCIP Python compatibility patch failed its SHA-256 check.");
            }

            File.Replace(temporaryPath, bundlePath, destinationBackupFileName: null);
            var installedHash = ComputeSha256(bundlePath);
            if (!string.Equals(installedHash, patchedBundleSha256, StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    "The installed SCIP Python compatibility patch failed its SHA-256 check.");
            }
        }
        finally
        {
            File.Delete(temporaryPath);
        }

        return ScipPythonPatchOutcome.Applied;
    }

    private static void VerifyPackageVersion(string packageRoot, string expectedVersion)
    {
        var manifestPath = Path.Combine(packageRoot, "package.json");
        if (!File.Exists(manifestPath))
        {
            throw new InvalidOperationException(
                $"The SCIP Python package manifest was not found at '{manifestPath}'.");
        }

        using var manifest = JsonDocument.Parse(File.ReadAllText(manifestPath));
        var actualVersion = manifest.RootElement.TryGetProperty("version", out var version)
            ? version.GetString()
            : null;
        if (!string.Equals(actualVersion, expectedVersion, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Expected SCIP Python {expectedVersion}, but npm installed " +
                $"'{actualVersion ?? "an unknown version"}'.");
        }
    }

    private static string ComputeSha256(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream));
    }

    private static int CountOccurrences(string source, string value)
    {
        var count = 0;
        var position = 0;
        while ((position = source.IndexOf(value, position, StringComparison.Ordinal)) >= 0)
        {
            count++;
            position += value.Length;
        }

        return count;
    }
}
