using System.Collections.ObjectModel;
using System.Diagnostics.CodeAnalysis;

namespace LocalAi.Installer.Core.Dependencies;

public enum DependencyVersionPolicy
{
    ExactRequestedVersion,
}

public sealed record DependencyDefinition(
    string ActionId,
    string DisplayName,
    string ExecutableName,
    string PackageId,
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

    public static IReadOnlyList<DependencyDefinition> Supported { get; } =
        new ReadOnlyCollection<DependencyDefinition>([Git, Ollama, GitHubCli]);

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
            DependencyVersionPolicy.ExactRequestedVersion,
            uri);
    }
}
