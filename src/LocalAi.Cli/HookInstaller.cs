using System.Text;

namespace LocalAi.Cli;

public sealed record HookInstallResult(
    IReadOnlyList<string> Installed,
    IReadOnlyList<string> Chained,
    string HooksDirectory,
    bool InsideWorkingTree);

public static class HookInstaller
{
    private const string ExcludeHeader = "# LocalAi managed Git hooks";

    private static readonly string[] Events =
    [
        "post-commit",
        "post-merge",
        "post-rewrite",
        "post-checkout"
    ];

    /// <summary>
    /// Where Git will actually look for hooks in this repository.
    /// </summary>
    /// <remarks>
    /// Writing to <c>$GIT_DIR/hooks</c> unconditionally is wrong for any repository that sets
    /// <c>core.hooksPath</c>, which is most front-end ones: husky, lefthook and simple-git-hooks
    /// all set it. Git then never looks at <c>$GIT_DIR/hooks</c>, so the dispatchers install
    /// successfully and never run again, and the index falls behind HEAD with nothing to say so.
    /// </remarks>
    public static string ResolveHooksDirectory(
        string commonDirectory,
        string? configuredHooksPath,
        string? workingTreeRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(commonDirectory);
        var common = Path.GetFullPath(commonDirectory);
        if (string.IsNullOrWhiteSpace(configuredHooksPath))
        {
            return Path.Combine(common, "hooks");
        }

        // Git resolves a relative core.hooksPath against the directory a hook runs in, which is
        // the top of the working tree. A bare repository has none, and then the common directory
        // is the only thing left to resolve against.
        var configured = configuredHooksPath.Trim();
        var resolved = Path.IsPathRooted(configured)
            ? Path.GetFullPath(configured)
            : Path.GetFullPath(Path.Combine(
                string.IsNullOrWhiteSpace(workingTreeRoot) ? common : workingTreeRoot,
                configured));

        // husky points core.hooksPath at `.husky/_`, a directory it rewrites from scratch on
        // every `husky` run — so an npm install would silently delete anything left there. Its
        // shims run `.husky/<hook>` instead, which husky never overwrites, and which every
        // installed shim already delegates to whether or not that file exists yet.
        return IsHuskyRunnerDirectory(resolved)
            ? Path.GetDirectoryName(resolved.TrimEnd(
                Path.DirectorySeparatorChar,
                Path.AltDirectorySeparatorChar))!
            : resolved;
    }

    private static bool IsHuskyRunnerDirectory(string directory)
    {
        var trimmed = directory.TrimEnd(
            Path.DirectorySeparatorChar,
            Path.AltDirectorySeparatorChar);
        if (!string.Equals(Path.GetFileName(trimmed), "_", StringComparison.Ordinal))
        {
            return false;
        }

        return Path.GetDirectoryName(trimmed) is { Length: > 0 } &&
               (File.Exists(Path.Combine(trimmed, "h")) ||
                File.Exists(Path.Combine(trimmed, "husky.sh")));
    }

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
                $"{commandPrefix} hook {hookEvent} " +
                "--root \"$(git rev-parse --show-toplevel)\"\n";
            File.WriteAllText(
                hookPath,
                script,
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            installed.Add(hookPath);
        }

        // `$GIT_DIR/hooks` sits under the working tree by path but is no part of it, so only a
        // hooks directory outside the Git directory can turn up in `git status`.
        var insideWorkingTree =
            IsInside(hooksRoot, workingTreeRoot) &&
            !IsInside(hooksRoot, commonDirectory);
        if (insideWorkingTree)
        {
            Exclude(commonDirectory, workingTreeRoot!, installed);
        }

        return new HookInstallResult(installed, chained, hooksRoot, insideWorkingTree);
    }

    private static bool IsInside(string path, string? container)
    {
        if (string.IsNullOrWhiteSpace(container))
        {
            return false;
        }

        var root = Path.GetFullPath(container)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return Path.GetFullPath(path).StartsWith(
            root + Path.DirectorySeparatorChar,
            StringComparison.OrdinalIgnoreCase);
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
        var root = Path.GetFullPath(workingTreeRoot);
        var patterns = installed
            .Select(path => "/" + Path.GetRelativePath(root, path).Replace('\\', '/'))
            .SelectMany(pattern => new[] { pattern, pattern + ".pre-localai" })
            .ToArray();
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

        if (!lines.Contains(ExcludeHeader, StringComparer.Ordinal))
        {
            lines.Add(ExcludeHeader);
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
