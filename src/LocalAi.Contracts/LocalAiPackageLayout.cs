namespace LocalAi.Contracts;

public static class LocalAiPackageLayout
{
    public const string StableLauncherFile = "localai-launcher.exe";

    public static IReadOnlyList<string> VersionRequiredFiles { get; } =
        Array.AsReadOnly(
        [
            "localai.exe",
            "codesearch.exe",
            "codesearch-mcp.exe",
            "locallm-mcp.exe",
            "LocalAi.Broker.dll",
            "LocalAi.Contracts.dll",
        ]);

    // Compatibility name used by runtime consumers. Package-only artifacts
    // must never be added here because version directories are immutable.
    public static IReadOnlyList<string> RequiredFiles => VersionRequiredFiles;

    public static IReadOnlyList<string> PackageArtifactFiles { get; } =
        Array.AsReadOnly(
            VersionRequiredFiles.Append(StableLauncherFile).ToArray());
}
