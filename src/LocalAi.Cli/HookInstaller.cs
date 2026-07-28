using System.Text;

namespace LocalAi.Cli;

public sealed record HookInstallResult(
    IReadOnlyList<string> Installed,
    IReadOnlyList<string> Chained);

public static class HookInstaller
{
    private static readonly string[] Events =
    [
        "post-commit",
        "post-merge",
        "post-rewrite",
        "post-checkout"
    ];

    public static HookInstallResult Install(
        string commonDirectory,
        string localAiExecutable)
    {
        var hooksRoot = Path.Combine(
            Path.GetFullPath(commonDirectory),
            "hooks");
        Directory.CreateDirectory(hooksRoot);
        var executable = Path.GetFullPath(localAiExecutable).Replace('\\', '/');
        var installed = new List<string>();
        var chained = new List<string>();

        foreach (var hookEvent in Events)
        {
            var hookPath = Path.Combine(hooksRoot, hookEvent);
            var previousPath = hookPath + ".pre-localai";
            if (File.Exists(hookPath) &&
                !File.ReadAllText(hookPath).Contains(
                    "# LocalAi managed dispatcher",
                    StringComparison.Ordinal))
            {
                if (File.Exists(previousPath))
                {
                    throw new InvalidOperationException(
                        $"Cannot safely chain hook because backup exists: {previousPath}");
                }

                File.Move(hookPath, previousPath);
                chained.Add(hookPath);
            }

            var previous = File.Exists(previousPath)
                ? $"\"{previousPath.Replace('\\', '/')}\" \"$@\"\n" +
                  "previous_status=$?\n" +
                  "if [ $previous_status -ne 0 ]; then exit $previous_status; fi\n"
                : string.Empty;
            var script =
                "#!/bin/sh\n" +
                "# LocalAi managed dispatcher\n" +
                previous +
                $"\"{executable}\" hook {hookEvent} --root \"$(git rev-parse --show-toplevel)\"\n";
            File.WriteAllText(
                hookPath,
                script,
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            installed.Add(hookPath);
        }

        return new HookInstallResult(installed, chained);
    }
}
