namespace LocalAi.Launcher;

using LocalAi.Contracts;

public static class LauncherLayout
{
    public static IReadOnlyDictionary<string, string> Tools { get; } =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["localai"] = "localai.exe",
            ["codesearch"] = "codesearch.exe",
            ["codesearch-mcp"] = "codesearch-mcp.exe",
            ["locallm-mcp"] = "locallm-mcp.exe"
        };

    public static IReadOnlyList<string> RequiredFiles =>
        LocalAiPackageLayout.RequiredFiles;
}
