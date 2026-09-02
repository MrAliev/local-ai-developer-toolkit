using System.Diagnostics;
using LocalAi.Contracts;

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

    /// <summary>The top of the working tree, or null when there is none (a bare repository).</summary>
    public async Task<string?> GetWorkingTreeRootAsync(
        string workingDirectory,
        CancellationToken cancellationToken = default)
    {
        var output = await TryRunAsync(
            workingDirectory,
            ["rev-parse", "--path-format=absolute", "--show-toplevel"],
            cancellationToken);
        return string.IsNullOrWhiteSpace(output) ? null : Path.GetFullPath(output.Trim());
    }

    /// <summary>One configuration value, or null when it is not set.</summary>
    public async Task<string?> GetConfigurationAsync(
        string workingDirectory,
        string name,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        var output = await TryRunAsync(
            workingDirectory,
            ["config", "--get", name],
            cancellationToken);
        return string.IsNullOrWhiteSpace(output) ? null : output.Trim();
    }

    /// <summary>
    /// Runs git where a non-zero exit is an answer rather than a failure: `config --get` exits 1
    /// for an unset key, and `--show-toplevel` fails in a bare repository.
    /// </summary>
    private static async Task<string?> TryRunAsync(
        string workingDirectory,
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken)
    {
        var (exitCode, stdout, _) = await ExecuteAsync(
            workingDirectory,
            arguments,
            cancellationToken);
        return exitCode == 0 ? stdout : null;
    }

    private static async Task<string> RunAsync(
        string workingDirectory,
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken)
    {
        var (exitCode, stdout, stderr) = await ExecuteAsync(
            workingDirectory,
            arguments,
            cancellationToken);
        return exitCode == 0
            ? stdout
            : throw new InvalidOperationException(
                $"git exited with {exitCode}: {stderr.Trim()}");
    }

    private static async Task<(int ExitCode, string StandardOutput, string StandardError)>
        ExecuteAsync(
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
            CreateNoWindow = true,
            // Git writes UTF-8 on every platform. Inherited from the console instead, a path
            // with non-ASCII in it came back wrong on any machine that had not overridden it.
            StandardOutputEncoding = ChildProcessText.Utf8,
            StandardErrorEncoding = ChildProcessText.Utf8,
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
        return (process.ExitCode, await stdout, await stderr);
    }
}
