using System.ComponentModel;
using System.Diagnostics;

namespace LocalAi.Launcher;

public sealed class ToolRunner
{
    private readonly string _launcherPath;

    public ToolRunner(string launcherPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(launcherPath);
        _launcherPath = Path.GetFullPath(launcherPath);
    }

    public async Task<int> RunAsync(
        string executablePath,
        IReadOnlyList<string> arguments,
        string version,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(executablePath);
        ArgumentNullException.ThrowIfNull(arguments);
        ArgumentException.ThrowIfNullOrWhiteSpace(version);
        var startInfo = new ProcessStartInfo(Path.GetFullPath(executablePath))
        {
            UseShellExecute = false,
            RedirectStandardInput = false,
            RedirectStandardOutput = false,
            RedirectStandardError = false,
            CreateNoWindow = true
        };
        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        startInfo.Environment["LOCALAI_LAUNCHER_PATH"] = _launcherPath;
        startInfo.Environment["LOCALAI_ACTIVE_VERSION"] = version;
        Process process;
        try
        {
            process = Process.Start(startInfo)
                ?? throw new LauncherException(
                    "child_start_failed",
                    $"Could not start LocalAi tool '{executablePath}'.");
        }
        catch (Win32Exception exception)
        {
            throw new LauncherException(
                "child_start_failed",
                $"Could not start LocalAi tool '{executablePath}': {exception.Message}");
        }

        using (process)
        {
            using var cancellation = cancellationToken.Register(() =>
            {
                try
                {
                    if (!process.HasExited)
                    {
                        process.Kill(entireProcessTree: true);
                    }
                }
                catch (InvalidOperationException)
                {
                }
            });
            await process.WaitForExitAsync(cancellationToken);
            return process.ExitCode;
        }
    }
}
