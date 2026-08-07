using LocalAi.Installer.Core.Abstractions;
using LocalAi.Installer.Core.Diagnosis;

namespace LocalAi.Installer.Core.Tests;

public sealed class SystemEnvironmentProbeTests
{
    [Fact]
    public void Windows_command_candidates_prefer_executable_shims_over_extensionless_scripts()
    {
        var candidates = SystemEnvironmentProbe.CandidateNames(
            "scip-typescript",
            isWindows: true,
            ".COM;.EXE;.BAT;.CMD");

        Assert.Equal(
            [
                "scip-typescript.COM",
                "scip-typescript.EXE",
                "scip-typescript.BAT",
                "scip-typescript.CMD",
                "scip-typescript",
            ],
            candidates);
    }

    [Fact]
    public void Explicit_extensions_and_non_windows_commands_are_unchanged()
    {
        Assert.Equal(
            ["npm.cmd"],
            SystemEnvironmentProbe.CandidateNames(
                "npm.cmd",
                isWindows: true,
                ".COM;.EXE;.BAT;.CMD"));
        Assert.Equal(
            ["python3"],
            SystemEnvironmentProbe.CandidateNames(
                "python3",
                isWindows: false,
                null));
    }

    [Fact]
    public void Installed_npm_scip_commands_resolve_to_windows_shims()
    {
        if (!OperatingSystem.IsWindows())
        {
            Assert.Skip("npm Windows shim precedence is Windows-specific.");
        }

        var npmDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "npm");
        var commands = new[] { "scip-typescript", "scip-python" };
        if (commands.Any(command =>
                !File.Exists(Path.Combine(npmDirectory, command)) ||
                !File.Exists(Path.Combine(npmDirectory, command + ".cmd"))))
        {
            Assert.Skip("Install both SCIP npm packages to run the real shim test.");
        }

        var probe = new SystemEnvironmentProbe();
        Assert.All(commands, command =>
            Assert.EndsWith(
                command + ".cmd",
                probe.ResolveExecutable(command),
                StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Installed_npm_scip_shims_execute_the_same_version_probes_as_the_installer()
    {
        if (!OperatingSystem.IsWindows())
        {
            Assert.Skip("npm Windows shim execution is Windows-specific.");
        }

        var probe = new SystemEnvironmentProbe();
        var runner = new SystemProcessRunner();
        foreach (var (command, expectedVersion) in new[]
                 {
                     ("scip-typescript", "0.4.0"),
                     ("scip-python", "0.6.6"),
                 })
        {
            var executable = probe.ResolveExecutable(command);
            if (executable is null)
            {
                Assert.Skip($"Install {command} to run the real version probe.");
            }

            var result = await runner.RunAsync(
                executable,
                ["--version"],
                TimeSpan.FromSeconds(5),
                TestContext.Current.CancellationToken);

            Assert.Equal(0, result.ExitCode);
            Assert.False(result.TimedOut);
            Assert.False(result.Cancelled);
            Assert.Contains(
                expectedVersion,
                result.StandardOutput + result.StandardError,
                StringComparison.Ordinal);
        }
    }
}
