using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using LocalAi.Installer.Core.Abstractions;

namespace LocalAi.Installer.Core.Diagnosis;

public sealed class WindowsEnvironmentDetector(
    IEnvironmentProbe environment,
    IFileSystemProbe fileSystem,
    IProcessRunner processRunner,
    IInstalledApplicationProbe installedApplications,
    IDiskProbe diskProbe,
    INetworkProbe networkProbe,
    IWindowsGpuProbe gpuProbe)
{
    private static readonly TimeSpan VersionProbeTimeout = TimeSpan.FromSeconds(5);

    public async Task<EnvironmentDiagnosis> DetectAsync(
        CancellationToken cancellationToken)
    {
        var host = environment.GetHost();
        var osSupported = host.IsWindows &&
                          host.Version.Major == 10 &&
                          (host.ProductName.StartsWith("Windows 10", StringComparison.OrdinalIgnoreCase) ||
                           host.ProductName.StartsWith("Windows 11", StringComparison.OrdinalIgnoreCase));
        var architectureSupported = host.Architecture == Architecture.X64;
        var operatingSystem = new OperatingSystemSnapshot(
            host.ProductName,
            host.Version,
            host.Architecture,
            osSupported ? SupportStatus.Supported : SupportStatus.Unsupported,
            architectureSupported ? SupportStatus.Supported : SupportStatus.Unsupported);
        var unsupportedReasons = new List<string>();
        if (!osSupported)
        {
            unsupportedReasons.Add(
                $"Only Windows 10/11 is supported; detected '{host.ProductName}'.");
        }

        if (!architectureSupported)
        {
            unsupportedReasons.Add(
                $"Only x64 is supported; detected '{host.Architecture}'.");
        }

        var winGet = await DetectCommandDependencyAsync(
                "WinGet",
                "winget.exe",
                cancellationToken)
            .ConfigureAwait(false);
        var git = await DetectCommandDependencyAsync(
                "Git",
                "git.exe",
                cancellationToken)
            .ConfigureAwait(false);
        var gitHubCli = await DetectCommandDependencyAsync(
                "GitHubCli",
                "gh.exe",
                cancellationToken)
            .ConfigureAwait(false);
        var ollama = await DetectOllamaAsync(cancellationToken).ConfigureAwait(false);
        var dotNetSdk = await DetectVersionedCommandDependencyAsync(
                "DotNetSdk",
                "dotnet.exe",
                "10",
                cancellationToken)
            .ConfigureAwait(false);
        var nodeJs = await DetectVersionedCommandDependencyAsync(
                "NodeJs",
                "node.exe",
                "20",
                cancellationToken)
            .ConfigureAwait(false);
        var npm = await DetectCommandDependencyAsync(
                "Npm",
                "npm.cmd",
                cancellationToken)
            .ConfigureAwait(false);
        var scipTypeScript = await DetectVersionedCommandDependencyAsync(
                "ScipTypeScript",
                "scip-typescript",
                "0.4.0",
                cancellationToken)
            .ConfigureAwait(false);
        var python = await DetectMinimumVersionCommandDependencyAsync(
                "Python",
                "python.exe",
                new Version(3, 10),
                cancellationToken)
            .ConfigureAwait(false);
        var scipPython = await DetectVersionedCommandDependencyAsync(
                "ScipPython",
                "scip-python",
                "0.6.6",
                cancellationToken)
            .ConfigureAwait(false);
        var disk = diskProbe.Observe(environment.LocalAppData);
        var network = await networkProbe.ObserveAsync(cancellationToken).ConfigureAwait(false);
        var gpu = await gpuProbe.ProbeAsync(cancellationToken).ConfigureAwait(false);
        var existing = new ExistingLocalAiInspector(fileSystem).Inspect(
            environment.LocalAppData);
        var agents = new[]
        {
            DetectAgent(
                AgentKind.Codex,
                "codex.exe",
                Path.Combine(environment.UserProfile, ".codex", "config.toml"),
                Path.Combine(environment.UserProfile, ".codex", "AGENTS.md")),
            DetectAgent(
                AgentKind.Claude,
                "claude.exe",
                Path.Combine(environment.UserProfile, ".claude.json"),
                Path.Combine(environment.UserProfile, ".claude", "CLAUDE.md")),
        };

        return new EnvironmentDiagnosis(
            operatingSystem,
            disk,
            network,
            winGet,
            git,
            gitHubCli,
            ollama,
            dotNetSdk,
            nodeJs,
            npm,
            scipTypeScript,
            python,
            scipPython,
            gpu,
            existing,
            agents,
            unsupportedReasons);
    }

    private async Task<DependencySnapshot> DetectCommandDependencyAsync(
        string name,
        string executableName,
        CancellationToken cancellationToken)
    {
        var executablePath = environment.ResolveExecutable(executableName);
        if (executablePath is null)
        {
            return new DependencySnapshot(
                name,
                DependencyState.NotFound,
                null,
                null,
                null);
        }

        try
        {
            var result = await processRunner.RunAsync(
                    executablePath,
                    ["--version"],
                    VersionProbeTimeout,
                    cancellationToken)
                .ConfigureAwait(false);
            if (result.ExitCode == 0 && !result.TimedOut && !result.Cancelled)
            {
                var version = string.IsNullOrWhiteSpace(result.StandardOutput)
                    ? result.StandardError.Trim()
                    : result.StandardOutput.Trim();
                return new DependencySnapshot(
                    name,
                    DependencyState.Detected,
                    executablePath,
                    version,
                    null);
            }

            var reason = result.TimedOut
                ? "Version probe timed out."
                : result.Cancelled
                    ? "Version probe was cancelled."
                    : result.StandardError.Trim();
            return new DependencySnapshot(
                name,
                DependencyState.Failed,
                executablePath,
                null,
                string.IsNullOrWhiteSpace(reason)
                    ? "Version probe failed."
                    : reason);
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or
            System.ComponentModel.Win32Exception)
        {
            return new DependencySnapshot(
                name,
                DependencyState.Failed,
                executablePath,
                null,
                exception.Message);
        }
    }

    private async Task<DependencySnapshot> DetectVersionedCommandDependencyAsync(
        string name,
        string executableName,
        string requiredVersionPrefix,
        CancellationToken cancellationToken)
    {
        var snapshot = await DetectCommandDependencyAsync(
            name,
            executableName,
            cancellationToken).ConfigureAwait(false);
        if (snapshot.State != DependencyState.Detected)
        {
            return snapshot;
        }

        var match = Regex.Match(
            snapshot.Version ?? string.Empty,
            @"\b(?:v)?([0-9]+(?:\.[0-9]+){0,3})\b",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        var detected = match.Success ? match.Groups[1].Value : null;
        var compatible = detected is not null &&
            (string.Equals(detected, requiredVersionPrefix, StringComparison.Ordinal) ||
             detected.StartsWith(requiredVersionPrefix + ".", StringComparison.Ordinal));
        return compatible
            ? snapshot
            : new DependencySnapshot(
                name,
                DependencyState.Failed,
                snapshot.ExecutablePath,
                snapshot.Version,
                $"Version {requiredVersionPrefix} is required.");
    }

    private async Task<DependencySnapshot> DetectMinimumVersionCommandDependencyAsync(
        string name,
        string executableName,
        Version minimumVersion,
        CancellationToken cancellationToken)
    {
        var snapshot = await DetectCommandDependencyAsync(
            name,
            executableName,
            cancellationToken).ConfigureAwait(false);
        if (snapshot.State != DependencyState.Detected)
        {
            return snapshot;
        }

        var match = Regex.Match(
            snapshot.Version ?? string.Empty,
            @"\b([0-9]+(?:\.[0-9]+){1,3})\b",
            RegexOptions.CultureInvariant);
        var compatible = match.Success &&
            Version.TryParse(match.Groups[1].Value, out var detected) &&
            detected >= minimumVersion;
        return compatible
            ? snapshot
            : new DependencySnapshot(
                name,
                DependencyState.Failed,
                snapshot.ExecutablePath,
                snapshot.Version,
                $"Version {minimumVersion.Major}.{minimumVersion.Minor} or newer is required.");
    }

    private async Task<DependencySnapshot> DetectOllamaAsync(
        CancellationToken cancellationToken)
    {
        var installed = await installedApplications
            .FindOllamaAsync(cancellationToken)
            .ConfigureAwait(false);
        var usable = installed is not null &&
                     !string.IsNullOrWhiteSpace(installed.ExecutablePath) &&
                     string.Equals(
                         Path.GetFileName(installed.ExecutablePath),
                         "ollama.exe",
                         StringComparison.OrdinalIgnoreCase) &&
                     !string.IsNullOrWhiteSpace(installed.DetectedVersion);
        if (usable)
        {
            return new DependencySnapshot(
                "Ollama",
                DependencyState.Detected,
                installed!.ExecutablePath,
                installed.DetectedVersion,
                null);
        }

        var commandDependency = await DetectCommandDependencyAsync(
            "Ollama",
            "ollama.exe",
            cancellationToken).ConfigureAwait(false);
        return commandDependency.State switch
        {
            DependencyState.Detected =>
                new DependencySnapshot(
                    "Ollama",
                    DependencyState.Detected,
                    commandDependency.ExecutablePath,
                    NormalizeOllamaVersion(commandDependency.Version),
                    null),
            DependencyState.Failed =>
                new DependencySnapshot(
                    "Ollama",
                    DependencyState.Failed,
                    commandDependency.ExecutablePath,
                    null,
                    commandDependency.Reason),
            _ => new DependencySnapshot(
                "Ollama",
                DependencyState.NotFound,
                null,
                null,
                null),
        };
    }

    private static string? NormalizeOllamaVersion(string? rawVersion)
    {
        if (string.IsNullOrWhiteSpace(rawVersion))
        {
            return null;
        }

        var match = Regex.Match(
            rawVersion,
            @"\b([0-9]{1,5}(?:\.[0-9]{1,5}){1,3}(?:[-+][0-9A-Za-z](?:[0-9A-Za-z.-]{0,30}[0-9A-Za-z])?)?)\b",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        return match.Success ? match.Value : rawVersion.Trim();
    }

    private AgentSnapshot DetectAgent(
        AgentKind kind,
        string executableName,
        string configPath,
        string instructionsPath)
    {
        var executablePath = environment.ResolveExecutable(executableName);
        return new AgentSnapshot(
            kind,
            executablePath is null
                ? FileMetadataSnapshot.Absent(executableName)
                : fileSystem.GetMetadata(executablePath),
            fileSystem.GetMetadata(configPath),
            fileSystem.GetMetadata(instructionsPath));
    }
}
