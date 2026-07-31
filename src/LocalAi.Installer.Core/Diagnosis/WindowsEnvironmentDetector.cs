using System.Runtime.InteropServices;
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
        var ollama = await DetectOllamaAsync(cancellationToken).ConfigureAwait(false);
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
            ollama,
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
                return new DependencySnapshot(
                    name,
                    DependencyState.Detected,
                    executablePath,
                    result.StandardOutput.Trim(),
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
        return !usable
            ? new DependencySnapshot(
                "Ollama",
                DependencyState.NotFound,
                null,
                null,
                null)
            : new DependencySnapshot(
                "Ollama",
                DependencyState.Detected,
                installed!.ExecutablePath,
                installed.DetectedVersion,
                null);
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
