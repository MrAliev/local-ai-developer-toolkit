using System.Collections.ObjectModel;
using System.Diagnostics.CodeAnalysis;

namespace LocalAi.Installer.Core.Dependencies;

public enum DependencyVersionPolicy
{
    ExactRequestedVersion,
}

public enum DependencyInstallerKind
{
    WinGet,
    Npm,
}

public sealed record DependencyDefinition(
    string ActionId,
    string DisplayName,
    string ExecutableName,
    string PackageId,
    DependencyInstallerKind InstallerKind,
    string? PackageVersion,
    DependencyVersionPolicy VersionPolicy,
    Uri OfficialInstallerUri);

public static class DependencyCatalog
{
    public static DependencyDefinition Git { get; } = Create(
        "dependency.git",
        "Git",
        "git.exe",
        "Git.Git",
        "https://git-scm.com/install/windows");

    public static DependencyDefinition Ollama { get; } = Create(
        "dependency.ollama",
        "Ollama",
        "ollama.exe",
        "Ollama.Ollama",
        "https://ollama.com/download/windows");

    /// <summary>
    /// Needed to download release assets from a private repository. The installer never
    /// handles a token itself: it reuses the sign-in the user already established with
    /// <c>gh auth login</c> on that machine.
    /// </summary>
    public static DependencyDefinition GitHubCli { get; } = Create(
        "dependency.github-cli",
        "GitHub CLI",
        "gh.exe",
        "GitHub.cli",
        "https://cli.github.com/");

    public static DependencyDefinition NodeJs { get; } = Create(
        "dependency.nodejs-20",
        "Node.js 20",
        "node.exe",
        "OpenJS.NodeJS.20",
        "https://nodejs.org/en/download");

    public static DependencyDefinition DotNetSdk { get; } = Create(
        "dependency.dotnet-sdk-10",
        ".NET SDK 10",
        "dotnet.exe",
        "Microsoft.DotNet.SDK.10",
        "https://dotnet.microsoft.com/download/dotnet/10.0");

    public static DependencyDefinition ScipTypeScript { get; } = CreateNpm(
        "dependency.scip-typescript",
        "SCIP TypeScript",
        "scip-typescript",
        "@sourcegraph/scip-typescript",
        "0.4.0",
        "https://www.npmjs.com/package/@sourcegraph/scip-typescript");

    public static DependencyDefinition Python { get; } = Create(
        "dependency.python-3-12",
        "Python 3.10+",
        "python.exe",
        "Python.Python.3.12",
        "https://www.python.org/downloads/windows/");

    public static DependencyDefinition ScipPython { get; } = CreateNpm(
        "dependency.scip-python",
        "SCIP Python",
        "scip-python",
        "@sourcegraph/scip-python",
        "0.6.6",
        "https://www.npmjs.com/package/@sourcegraph/scip-python");

    public static IReadOnlyList<DependencyDefinition> Supported { get; } =
        new ReadOnlyCollection<DependencyDefinition>(
            [
                Git,
                Ollama,
                GitHubCli,
                DotNetSdk,
                NodeJs,
                ScipTypeScript,
                Python,
                ScipPython,
            ]);

    public static bool TryGetByPackageId(
        string? packageId,
        [NotNullWhen(true)]
        out DependencyDefinition? definition)
    {
        definition = Supported.FirstOrDefault(
            candidate => string.Equals(
                candidate.PackageId,
                packageId,
                StringComparison.Ordinal))!;
        return definition is not null;
    }

    internal static bool TryGetByActionId(
        string? actionId,
        [NotNullWhen(true)]
        out DependencyDefinition? definition)
    {
        definition = Supported.FirstOrDefault(
            candidate => string.Equals(
                candidate.ActionId,
                actionId,
                StringComparison.Ordinal))!;
        return definition is not null;
    }

    private static DependencyDefinition Create(
        string actionId,
        string displayName,
        string executableName,
        string packageId,
        string officialInstallerUri)
    {
        var uri = new Uri(officialInstallerUri, UriKind.Absolute);
        if (!string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"The official installer URI for '{packageId}' must use HTTPS.");
        }

        return new DependencyDefinition(
            actionId,
            displayName,
            executableName,
            packageId,
            DependencyInstallerKind.WinGet,
            null,
            DependencyVersionPolicy.ExactRequestedVersion,
            uri);
    }

    private static DependencyDefinition CreateNpm(
        string actionId,
        string displayName,
        string executableName,
        string packageId,
        string packageVersion,
        string officialInstallerUri)
    {
        var uri = new Uri(officialInstallerUri, UriKind.Absolute);
        if (!string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"The official installer URI for '{packageId}' must use HTTPS.");
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(packageVersion);
        return new DependencyDefinition(
            actionId,
            displayName,
            executableName,
            packageId,
            DependencyInstallerKind.Npm,
            packageVersion,
            DependencyVersionPolicy.ExactRequestedVersion,
            uri);
    }
}
