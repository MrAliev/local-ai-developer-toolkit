using System.Diagnostics;

namespace LocalAi.Repository;

public sealed class GitClient
{
    public async Task<string> GetCommonDirectoryAsync(
        string workingDirectory,
        CancellationToken cancellationToken = default)
    {
        var output = await RunAsync(
            workingDirectory,
            ["rev-parse", "--path-format=absolute", "--git-common-dir"],
            cancellationToken);
        return Path.GetFullPath(output.Trim());
    }

    public async Task<string> GetTreeAsync(
        string workingDirectory,
        string revision,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(revision);
        return (await RunAsync(
            workingDirectory,
            ["rev-parse", $"{revision}^{{tree}}"],
            cancellationToken)).Trim();
    }

    private static async Task<string> RunAsync(
        string workingDirectory,
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken)
    {
        var start = new ProcessStartInfo("git")
        {
            WorkingDirectory = Path.GetFullPath(workingDirectory),
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        foreach (var argument in arguments)
        {
            start.ArgumentList.Add(argument);
        }

        using var process = Process.Start(start)
            ?? throw new InvalidOperationException("Could not start git.");
        var stdout = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var stderr = process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken);
        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"git exited with {process.ExitCode}: {(await stderr).Trim()}");
        }

        return await stdout;
    }
}
