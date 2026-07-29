using System.ComponentModel;
using System.Diagnostics;

namespace LocalAi.Launcher;

public sealed class ToolRunner
{
    private readonly string _launcherPath;
    private readonly Stream _standardOutput;
    private readonly Stream _standardError;

    public ToolRunner(string launcherPath)
        : this(
            launcherPath,
            Console.OpenStandardOutput(),
            Console.OpenStandardError())
    {
    }

    public ToolRunner(
        string launcherPath,
        Stream standardOutput,
        Stream standardError)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(launcherPath);
        _launcherPath = Path.GetFullPath(launcherPath);
        _standardOutput = standardOutput
            ?? throw new ArgumentNullException(nameof(standardOutput));
        _standardError = standardError
            ?? throw new ArgumentNullException(nameof(standardError));
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
            RedirectStandardOutput = true,
            RedirectStandardError = true,
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
            var standardOutput = CopyAndFlushAsync(
                process.StandardOutput.BaseStream,
                _standardOutput);
            var standardError = CopyAndFlushAsync(
                process.StandardError.BaseStream,
                _standardError);
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
            try
            {
                await process.WaitForExitAsync(cancellationToken);
            }
            finally
            {
                await Task.WhenAll(standardOutput, standardError);
            }

            return process.ExitCode;
        }
    }

    private static async Task CopyAndFlushAsync(
        Stream source,
        Stream destination)
    {
        await source.CopyToAsync(destination);
        await destination.FlushAsync();
    }
}
