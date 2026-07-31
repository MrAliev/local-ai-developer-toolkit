namespace LocalAi.Contracts;

public static class LocalAiPackageLayout
{
    public static IReadOnlyList<string> RequiredFiles { get; } =
        Array.AsReadOnly(
        [
            "localai.exe",
            "codesearch.exe",
            "codesearch-mcp.exe",
            "locallm-mcp.exe",
            "LocalAi.Broker.dll",
            "LocalAi.Contracts.dll",
        ]);
}
