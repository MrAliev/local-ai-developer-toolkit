using System.Text;
using LocalAi.Cli.Resources;
using LocalAi.Repository;

namespace LocalAi.Cli;

public sealed record HookInstallResult(
    IReadOnlyList<string> Installed,
    IReadOnlyList<string> Chained,
    string HooksDirectory,
    bool InsideWorkingTree);

public static class HookInstaller
{
    /// <inheritdoc cref="GitHookLayout.ResolveHooksDirectory"/>
    public static string ResolveHooksDirectory(
        string commonDirectory,
        string? configuredHooksPath,
        string? workingTreeRoot) =>
        GitHookLayout.ResolveHooksDirectory(
            commonDirectory,
            configuredHooksPath,
            workingTreeRoot);

    public static HookInstallResult Install(
        string commonDirectory,
        string launcherPath,
        IReadOnlyList<string> launcherArguments,
        string? configuredHooksPath = null,
        string? workingTreeRoot = null)
    {
        if (string.IsNullOrWhiteSpace(launcherPath))
        {
            throw new ArgumentException(
                "A stable LocalAi launcher path is required.",
                nameof(launcherPath));
        }
        ArgumentNullException.ThrowIfNull(launcherArguments);
        if (launcherArguments.Any(string.IsNullOrWhiteSpace))
        {
            throw new ArgumentException(
                "Launcher arguments cannot contain blank values.",
                nameof(launcherArguments));
        }

        var hooksRoot = ResolveHooksDirectory(
            commonDirectory,
            configuredHooksPath,
            workingTreeRoot);
        foreach (var hookEvent in GitHookLayout.Events)
        {
            var candidate = Path.Combine(hooksRoot, hookEvent);
            var saved = candidate + GitHookLayout.ChainedSuffix;
            if (File.Exists(candidate) &&
                !GitHookLayout.IsManagedDispatcher(candidate) &&
                File.Exists(saved))
            {
                throw new InvalidOperationException(
                    CliText.HooksChainBlocked(candidate, saved));
            }
        }

        Directory.CreateDirectory(hooksRoot);
        var executable = Path.GetFullPath(launcherPath).Replace('\\', '/');
        var commandPrefix = QuoteExecutable(executable);
        if (launcherArguments.Count > 0)
        {
            commandPrefix += " " + string.Join(
                ' ',
                launcherArguments.Select(QuoteArgument));
        }

        var installed = new List<string>();
        var chained = new List<string>();

        foreach (var hookEvent in GitHookLayout.Events)
        {
            var hookPath = Path.Combine(hooksRoot, hookEvent);
            var previousPath = hookPath + GitHookLayout.ChainedSuffix;
            if (File.Exists(hookPath) && !GitHookLayout.IsManagedDispatcher(hookPath))
            {
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
                GitHookLayout.DispatcherMarker + "\n" +
                previous +
                $"{commandPrefix} hook {hookEvent} " +
                "--root \"$(git rev-parse --show-toplevel)\"\n";
            File.WriteAllText(
                hookPath,
                script,
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            installed.Add(hookPath);
        }

        var insideWorkingTree = GitHookLayout.IsInsideWorkingTree(
            hooksRoot,
            commonDirectory,
            workingTreeRoot);
        if (insideWorkingTree)
        {
            Exclude(commonDirectory, workingTreeRoot!, installed);
        }

        return new HookInstallResult(installed, chained, hooksRoot, insideWorkingTree);
    }

    /// <summary>
    /// Keeps dispatchers written into the working tree out of <c>git status</c>.
    /// </summary>
    /// <remarks>
    /// A hooks directory like <c>.husky</c> is part of the working tree, so the dispatchers show
    /// up as untracked files in a repository that is not ours to add ignore rules to. This writes
    /// them into <c>.git/info/exclude</c> instead, which is per-clone and tracked by nothing.
    /// </remarks>
    private static void Exclude(
        string commonDirectory,
        string workingTreeRoot,
        IReadOnlyList<string> installed)
    {
        var patterns = GitHookLayout.ExcludePatterns(workingTreeRoot, installed);
        var excludePath = Path.Combine(
            Path.GetFullPath(commonDirectory),
            "info",
            "exclude");
        Directory.CreateDirectory(Path.GetDirectoryName(excludePath)!);
        var lines = File.Exists(excludePath)
            ? File.ReadAllLines(excludePath).ToList()
            : [];
        var missing = patterns
            .Where(pattern => !lines.Contains(pattern, StringComparer.Ordinal))
            .ToArray();
        if (missing.Length == 0)
        {
            return;
        }

        if (lines.Count > 0 && lines[^1].Length > 0)
        {
            lines.Add(string.Empty);
        }

        if (!lines.Contains(GitHookLayout.ExcludeHeader, StringComparer.Ordinal))
        {
            lines.Add(GitHookLayout.ExcludeHeader);
        }

        lines.AddRange(missing);
        File.WriteAllText(
            excludePath,
            string.Join('\n', lines) + "\n",
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
    }

    private static string QuoteExecutable(string value) =>
        "\"" +
        value
            .Replace("`", "\\`")
            .Replace("$", "\\$")
            .Replace("\"", "\\\"") +
        "\"";

    private static string QuoteArgument(string value) =>
        value.All(character =>
            char.IsAsciiLetterOrDigit(character) ||
            character is '_' or '-' or '.')
                ? value
                : "'" + value.Replace("'", "'\"'\"'") + "'";
}
