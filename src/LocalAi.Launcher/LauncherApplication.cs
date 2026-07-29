namespace LocalAi.Launcher;

public sealed class LauncherApplication
{
    private readonly string _binRoot;
    private readonly VersionResolver _resolver;
    private readonly ToolRunner _runner;

    public LauncherApplication(string binRoot, string launcherPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(binRoot);
        _binRoot = Path.GetFullPath(binRoot);
        _resolver = new VersionResolver(_binRoot);
        _runner = new ToolRunner(launcherPath);
    }

    public async Task<int> RunAsync(
        string tool,
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken)
    {
        using var lease = VersionLease.AcquireShared(
            Path.Combine(_binRoot, "current.lock"));
        var resolved = _resolver.Resolve(tool);
        return await _runner.RunAsync(
            resolved.ExecutablePath,
            arguments,
            resolved.Version,
            cancellationToken);
    }
}
