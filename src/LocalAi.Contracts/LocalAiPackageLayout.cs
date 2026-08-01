namespace LocalAi.Contracts;

public static class LocalAiPackageLayout
{
    public const string StableLauncherFile = "localai-launcher.exe";

    public const string BrokerFile = "LocalAi.Broker.exe";

    /// <summary>
    /// Every component ships as a self-contained executable, so an installed machine never
    /// needs a system-wide .NET runtime. Loose dependency assemblies are deliberately absent:
    /// they are bundled inside each executable, and the package format admits no extra files.
    /// </summary>
    public static IReadOnlyList<string> VersionRequiredFiles { get; } =
        Array.AsReadOnly(
        [
            "localai.exe",
            "codesearch.exe",
            "codesearch-mcp.exe",
            "locallm-mcp.exe",
            BrokerFile,
        ]);

    // Compatibility name used by runtime consumers. Package-only artifacts
    // must never be added here because version directories are immutable.
    public static IReadOnlyList<string> RequiredFiles => VersionRequiredFiles;

    public static IReadOnlyList<string> PackageArtifactFiles { get; } =
        Array.AsReadOnly(
            VersionRequiredFiles.Append(StableLauncherFile).ToArray());
}
