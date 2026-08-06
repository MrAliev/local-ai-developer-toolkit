using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using System.Text.Json.Serialization;

namespace CodeSearch.Core.Semantics;

[JsonConverter(typeof(JsonStringEnumConverter<SemanticAdapterState>))]
public enum SemanticAdapterState
{
    Succeeded,
    Failed,
    Skipped,
}

[JsonConverter(typeof(JsonStringEnumConverter<ScipPositionEncoding>))]
public enum ScipPositionEncoding
{
    Utf8 = 1,
    Utf16 = 2,
    Utf32 = 3,
}

public sealed record SemanticAdapterStatus(
    string Name,
    SemanticAdapterState State,
    string Message,
    long DurationMilliseconds,
    bool OutputTruncated = false);

public sealed record ScipAdapterSpec(
    string Name,
    string Executable,
    IReadOnlyList<string> Arguments,
    string OutputFile = "index.scip",
    TimeSpan? Timeout = null,
    int MaximumOutputBytes = 1024 * 1024,
    ScipPositionEncoding? UnspecifiedPositionEncoding = null)
{
    public TimeSpan EffectiveTimeout => Timeout ?? TimeSpan.FromMinutes(5);

    internal void Validate()
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(Name);
        ArgumentException.ThrowIfNullOrWhiteSpace(Executable);
        ArgumentNullException.ThrowIfNull(Arguments);
        if (Path.IsPathRooted(OutputFile) ||
            OutputFile.Replace('\\', '/').Split('/').Any(
                segment => segment is "" or "." or ".."))
        {
            throw new ArgumentException("SCIP adapter output must be a canonical relative path.");
        }

        if (EffectiveTimeout <= TimeSpan.Zero || MaximumOutputBytes <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(ScipAdapterSpec));
        }


        if (UnspecifiedPositionEncoding is not null &&
            !Enum.IsDefined(UnspecifiedPositionEncoding.Value))
        {
            throw new ArgumentOutOfRangeException(nameof(UnspecifiedPositionEncoding));
        }
    }
}

public sealed record ScipAdapterRunResult(
    SemanticIndex Index,
    SemanticAdapterStatus Status);

/// <summary>
/// Runs an explicitly configured SCIP indexer as a bounded child process and imports its output.
/// The source tree is never overwritten: an existing output artifact causes the adapter to skip.
/// </summary>
public sealed class ScipAdapterRunner(ScipImporter? importer = null)
{
    private readonly ScipImporter _importer = importer ?? new ScipImporter();

    public async Task<ScipAdapterRunResult> RunAsync(
        SemanticIndex current,
        string repositoryRoot,
        ScipAdapterSpec spec,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(current);
        ArgumentException.ThrowIfNullOrWhiteSpace(repositoryRoot);
        ArgumentNullException.ThrowIfNull(spec);
        spec.Validate();
        var root = Path.GetFullPath(repositoryRoot);
        var outputPath = Path.GetFullPath(Path.Combine(root, spec.OutputFile));
        if (!outputPath.StartsWith(
                Path.TrimEndingDirectorySeparator(root) + Path.DirectorySeparatorChar,
                OperatingSystem.IsWindows()
                    ? StringComparison.OrdinalIgnoreCase
                    : StringComparison.Ordinal))
        {
            throw new ArgumentException("SCIP adapter output escapes the repository root.");
        }

        if (File.Exists(outputPath))
        {
            return Result(
                current,
                spec,
                SemanticAdapterState.Skipped,
                $"Output '{spec.OutputFile}' already exists; it was not overwritten.",
                TimeSpan.Zero);
        }

        var arguments = spec.Arguments
            .Select(argument => Expand(argument, root))
            .ToArray();
        var executable = ResolveExecutable(spec.Executable);
        var isCommandScript = OperatingSystem.IsWindows() &&
            (executable.EndsWith(".cmd", StringComparison.OrdinalIgnoreCase) ||
             executable.EndsWith(".bat", StringComparison.OrdinalIgnoreCase));
        var start = new ProcessStartInfo(
            isCommandScript
                ? Environment.GetEnvironmentVariable("ComSpec") ?? "cmd.exe"
                : executable)
        {
            WorkingDirectory = root,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        if (isCommandScript)
        {
            try
            {
                start.Arguments =
                    "/d /s /c \"" + CommandScriptInvocation(executable, arguments) + "\"";
            }
            catch (ArgumentException exception)
            {
                return Result(
                    current,
                    spec,
                    SemanticAdapterState.Failed,
                    exception.Message,
                    TimeSpan.Zero);
            }
        }
        else
        {
            foreach (var argument in arguments)
            {
                start.ArgumentList.Add(argument);
            }
        }

        var stopwatch = Stopwatch.StartNew();
        using var process = new Process { StartInfo = start };
        try
        {
            try
            {
                if (!process.Start())
                {
                    return Result(current, spec, SemanticAdapterState.Failed,
                        "The indexer process did not start.", stopwatch.Elapsed);
                }
            }
            catch (Win32Exception exception) when (exception.NativeErrorCode is 2 or 3)
            {
                return Result(current, spec, SemanticAdapterState.Skipped,
                    $"Indexer executable '{spec.Executable}' is not installed.", stopwatch.Elapsed);
            }
            catch (Win32Exception exception)
            {
                return Result(current, spec, SemanticAdapterState.Failed,
                    $"Indexer process could not start: {exception.Message}", stopwatch.Elapsed);
            }

            var stdout = DrainAsync(process.StandardOutput.BaseStream, spec.MaximumOutputBytes);
            var stderr = DrainAsync(process.StandardError.BaseStream, spec.MaximumOutputBytes);
            using var timeout = new CancellationTokenSource(spec.EffectiveTimeout);
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken, timeout.Token);
            try
            {
                await process.WaitForExitAsync(linked.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (
                timeout.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
            {
                Kill(process);
                var timedOutput = await CompleteOutputAsync(stdout, stderr).ConfigureAwait(false);
                return Result(current, spec, SemanticAdapterState.Failed,
                    AppendOutput($"Timed out after {spec.EffectiveTimeout}.", timedOutput),
                    stopwatch.Elapsed, timedOutput.Truncated);
            }
            catch
            {
                Kill(process);
                throw;
            }

            var output = await CompleteOutputAsync(stdout, stderr).ConfigureAwait(false);
            if (process.ExitCode != 0)
            {
                return Result(current, spec, SemanticAdapterState.Failed,
                    AppendOutput($"Exited with code {process.ExitCode}.", output),
                    stopwatch.Elapsed, output.Truncated);
            }

            if (!File.Exists(outputPath))
            {
                return Result(current, spec, SemanticAdapterState.Failed,
                    AppendOutput($"Did not produce '{spec.OutputFile}'.", output),
                    stopwatch.Elapsed, output.Truncated);
            }

            try
            {
                await using var stream = new FileStream(
                    outputPath,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.Read,
                    81920,
                    FileOptions.Asynchronous | FileOptions.SequentialScan);
                var supplemented = _importer.Supplement(
                    current,
                    stream,
                    root,
                    spec.UnspecifiedPositionEncoding);
                return Result(supplemented, spec, SemanticAdapterState.Succeeded,
                    "SCIP output imported.", stopwatch.Elapsed, output.Truncated);
            }
            catch (Exception exception) when (
                exception is InvalidDataException or IOException or UnauthorizedAccessException)
            {
                return Result(current, spec, SemanticAdapterState.Failed,
                    $"SCIP import failed: {exception.Message}", stopwatch.Elapsed, output.Truncated);
            }
        }
        finally
        {
            if (File.Exists(outputPath))
            {
                File.Delete(outputPath);
            }
        }
    }

    private static string Expand(string argument, string root) =>
        argument.Replace(
            "{projectName}",
            SafeProjectName(new DirectoryInfo(root).Name),
            StringComparison.Ordinal);

    private static string SafeProjectName(string value)
    {
        var characters = value.Select(character =>
            character is >= 'a' and <= 'z' or >= 'A' and <= 'Z' or >= '0' and <= '9' or
                '-' or '_' or '.'
                ? character
                : '_').ToArray();
        return characters.Length == 0 ? "project" : new string(characters);
    }

    private static string ResolveExecutable(string executable)
    {
        if (Path.IsPathRooted(executable) ||
            executable.Contains(Path.DirectorySeparatorChar) ||
            executable.Contains(Path.AltDirectorySeparatorChar))
        {
            return ResolveCandidate(executable) ?? executable;
        }

        var path = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        foreach (var directory in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            var candidate = ResolveCandidate(Path.Combine(directory.Trim('"'), executable));
            if (candidate is not null)
            {
                return candidate;
            }
        }

        return executable;
    }

    private static string? ResolveCandidate(string candidate)
    {
        if (File.Exists(candidate))
        {
            return Path.GetFullPath(candidate);
        }

        if (!OperatingSystem.IsWindows() || Path.HasExtension(candidate))
        {
            return null;
        }

        foreach (var extension in new[] { ".exe", ".cmd", ".bat" })
        {
            if (File.Exists(candidate + extension))
            {
                return Path.GetFullPath(candidate + extension);
            }
        }

        return null;
    }

    private static string CommandScriptInvocation(
        string executable,
        IReadOnlyList<string> arguments)
    {
        var values = new[] { executable }.Concat(arguments).ToArray();
        if (values.Any(value => value.IndexOfAny(['"', '\r', '\n', '%', '!']) >= 0))
        {
            throw new ArgumentException(
                "Windows command-script adapter paths and arguments contain unsafe characters.");
        }

        return string.Join(" ", values.Select(value => $"\"{value}\""));
    }

    private static async Task<BoundedOutput> DrainAsync(Stream stream, int maximumBytes)
    {
        using var output = new MemoryStream(Math.Min(maximumBytes, 81920));
        var buffer = new byte[81920];
        var truncated = false;
        int read;
        while ((read = await stream.ReadAsync(buffer).ConfigureAwait(false)) > 0)
        {
            var remaining = maximumBytes - checked((int)output.Length);
            if (remaining > 0)
            {
                output.Write(buffer, 0, Math.Min(read, remaining));
            }

            truncated |= read > remaining;
        }

        return new BoundedOutput(
            Encoding.UTF8.GetString(output.GetBuffer(), 0, checked((int)output.Length)),
            truncated);
    }

    private static async Task<CombinedOutput> CompleteOutputAsync(
        Task<BoundedOutput> stdout,
        Task<BoundedOutput> stderr)
    {
        await Task.WhenAll(stdout, stderr).ConfigureAwait(false);
        return new CombinedOutput(
            (await stdout.ConfigureAwait(false)).Text,
            (await stderr.ConfigureAwait(false)).Text,
            stdout.Result.Truncated || stderr.Result.Truncated);
    }

    private static string AppendOutput(string message, CombinedOutput output)
    {
        var detail = string.Join(
            Environment.NewLine,
            new[] { output.StandardError, output.StandardOutput }
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .Select(value => value.Trim()));
        if (detail.Length == 0)
        {
            return message;
        }

        return message + " " + detail + (output.Truncated ? " [output truncated]" : string.Empty);
    }

    private static void Kill(Process process)
    {
        if (!process.HasExited)
        {
            process.Kill(entireProcessTree: true);
            process.WaitForExit();
        }
    }

    private static ScipAdapterRunResult Result(
        SemanticIndex index,
        ScipAdapterSpec spec,
        SemanticAdapterState state,
        string message,
        TimeSpan duration,
        bool truncated = false) =>
        new(index, new SemanticAdapterStatus(
            spec.Name,
            state,
            message,
            checked((long)duration.TotalMilliseconds),
            truncated));

    private sealed record BoundedOutput(string Text, bool Truncated);
    private sealed record CombinedOutput(
        string StandardOutput,
        string StandardError,
        bool Truncated);
}
