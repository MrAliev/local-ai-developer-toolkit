namespace LocalAi.Launcher.Tests;

internal sealed class TestInstall : IDisposable
{
    private static readonly string[] RequiredFiles =
    [
        "localai.exe",
        "codesearch.exe",
        "codesearch-mcp.exe",
        "locallm-mcp.exe",
        "LocalAi.Broker.dll",
        "LocalAi.Contracts.dll"
    ];

    private TestInstall(string root)
    {
        Root = root;
        BinRoot = Path.Combine(root, "bin");
        VersionsRoot = Path.Combine(BinRoot, "versions");
        CurrentPath = Path.Combine(BinRoot, "current.json");
        Directory.CreateDirectory(VersionsRoot);
    }

    public string Root { get; }

    public string BinRoot { get; }

    public string VersionsRoot { get; }

    public string CurrentPath { get; }

    public static TestInstall CreateComplete(params string[] versions)
    {
        var install = new TestInstall(Path.Combine(
            Path.GetTempPath(),
            "localai-launcher-tests-" + Guid.NewGuid().ToString("N")));
        foreach (var version in versions)
        {
            install.CreateVersion(version);
        }

        return install;
    }

    public string VersionDirectory(string version) =>
        Path.Combine(VersionsRoot, version);

    public void WriteCurrent(string json) =>
        File.WriteAllText(CurrentPath, json);

    public void RemoveRequiredFile(string version, string fileName) =>
        File.Delete(Path.Combine(VersionDirectory(version), fileName));

    public void CreateIncomplete(string version)
    {
        var directory = VersionDirectory(version);
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, "localai.exe"), "localai.exe");
    }

    public void ReplaceTool(string version, string toolFileName, string sourcePath) =>
        File.Copy(
            sourcePath,
            Path.Combine(VersionDirectory(version), toolFileName),
            overwrite: true);

    public void Dispose()
    {
        if (Directory.Exists(Root))
        {
            Directory.Delete(Root, recursive: true);
        }
    }

    private void CreateVersion(string version)
    {
        var directory = VersionDirectory(version);
        Directory.CreateDirectory(directory);
        foreach (var file in RequiredFiles)
        {
            File.WriteAllText(Path.Combine(directory, file), file);
        }
    }
}
