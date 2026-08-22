using LocalAi.Installer.Core.Abstractions;
using System.Text.Json;

namespace LocalAi.ReleaseSigner;

/// <summary>
/// The release, as one command instead of six steps held in someone's head.
///
/// What it replaces: write two note files, open and merge a pull request, run the publish script
/// with the version, sign, create the tag and release, upload four assets. Every step is fine on
/// its own; the cost is that the same version has to be typed identically in four places and the
/// commit has to be the same in two, and nothing checked either. A release built before the pull
/// request merged, or tagged at a different commit than it was built from, produces a package
/// that installs and is not the thing the tag names.
///
/// It deliberately stops before publishing. <see cref="PrepareAsync"/> takes it as far as a pull
/// request and says so; the merge is a human decision, and so is the release itself, which is why
/// <see cref="PublishAsync"/> is a separate invocation rather than a later stage of the same one.
/// Building and signing are repeatable and reversible. Tagging and publishing are neither: the
/// tag is what other people's installers resolve, and taking one back is not a thing you do
/// quietly.
///
/// Signing stays here rather than moving to CI, which is a decision recorded in
/// docs/release-signing-runbook.md and not a gap: the private key exists on one machine on
/// purpose, and a secrets store would be a second place it can leak from.
/// </summary>
public sealed class ReleaseCommand
{
    private static readonly TimeSpan QuickCommand = TimeSpan.FromMinutes(2);

    /// <summary>
    /// A self-contained publish of seven projects, twice over for the retry the script allows.
    /// Long enough that a real build never hits it, short enough that a hung one is not left
    /// occupying the machine overnight.
    /// </summary>
    private static readonly TimeSpan BuildCommand = TimeSpan.FromMinutes(60);

    private readonly IProcessRunner _runner;
    private readonly string _repositoryRoot;
    private readonly TextWriter _output;

    public ReleaseCommand(IProcessRunner runner, string repositoryRoot, TextWriter output)
    {
        _runner = runner ?? throw new ArgumentNullException(nameof(runner));
        ArgumentException.ThrowIfNullOrWhiteSpace(repositoryRoot);
        _repositoryRoot = Path.GetFullPath(repositoryRoot);
        _output = output ?? throw new ArgumentNullException(nameof(output));
    }

    /// <summary>
    /// Everything up to a pull request that carries the notes.
    ///
    /// Run once and it scaffolds whatever notes are missing and stops, because a release with
    /// nothing written about it is not ready by any definition. Run again once they are written
    /// and it opens the pull request.
    /// </summary>
    public async Task<int> PrepareAsync(
        ReleaseVersion version,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(version);
        if (!await RequireCleanTreeAsync(version, cancellationToken))
        {
            return 1;
        }

        await GitAsync(["fetch", "--tags", "--quiet", "origin"], cancellationToken);
        var newest = ReleaseVersion.Newest(
            (await GitAsync(["tag", "--list"], cancellationToken))
                .Split('\n', StringSplitOptions.RemoveEmptyEntries));
        if (newest is not null && version.CompareTo(newest) <= 0)
        {
            _output.WriteLine(
                $"{version} is not ahead of {newest}, which is already published. " +
                "Pick a higher version.");
            return 1;
        }

        var created = ReleaseNotes.Scaffold(_repositoryRoot, version);
        foreach (var path in created)
        {
            _output.WriteLine($"Created {path}");
        }

        var notes = ReleaseNotes.Inspect(_repositoryRoot, version);
        foreach (var problem in notes.Problems)
        {
            _output.WriteLine(problem);
        }

        if (!notes.ReadyToPublish)
        {
            _output.WriteLine(
                notes.StillTemplate
                    ? $"Write the release notes, then run this again. Both files still carry " +
                      $"\"{ReleaseNotes.TemplateMarker}\"."
                    : "Fix the release notes, then run this again.");
            return 2;
        }

        var branch = $"release/{version}";
        await GitAsync(["switch", "--create", branch], cancellationToken);
        await GitAsync(
            [
                "add",
                ReleaseNotes.EnglishPath(_repositoryRoot, version),
                ReleaseNotes.RussianPath(_repositoryRoot, version),
            ],
            cancellationToken);
        await GitAsync(["commit", "--message", $"Add {version} release notes"], cancellationToken);
        await GitAsync(["push", "--set-upstream", "origin", branch], cancellationToken);
        await RunAsync(
            "gh",
            [
                "pr", "create",
                "--base", "main",
                "--head", branch,
                "--title", $"Add {version} release notes",
                "--body-file", ReleaseNotes.EnglishPath(_repositoryRoot, version),
            ],
            QuickCommand,
            cancellationToken);

        _output.WriteLine(
            $"""
             Prepared {version}.

             Merge the pull request, return to main, and run:
               localai-release-signer release --version {version} --publish
             """);
        return 0;
    }

    /// <summary>
    /// Builds, signs, verifies and — only then — tags and publishes.
    ///
    /// The order is the point. Everything that can disagree is compared before anything leaves
    /// the machine, and the tag is created by the release itself at the exact commit the manifest
    /// was stamped from, rather than at whatever <c>main</c> happens to be when someone gets to
    /// that step.
    /// </summary>
    public async Task<int> PublishAsync(
        ReleaseVersion version,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(version);
        if (!await RequireCleanTreeAsync(version: null, cancellationToken))
        {
            return 1;
        }

        var branch = (await GitAsync(
            ["rev-parse", "--abbrev-ref", "HEAD"],
            cancellationToken)).Trim();
        if (!string.Equals(branch, "main", StringComparison.Ordinal))
        {
            _output.WriteLine(
                $"On branch {branch}. A release is published from main, at the commit that " +
                "merged its notes.");
            return 1;
        }

        await GitAsync(["fetch", "--tags", "--quiet", "origin"], cancellationToken);
        var head = (await GitAsync(["rev-parse", "HEAD"], cancellationToken)).Trim();
        var upstream = (await GitAsync(["rev-parse", "origin/main"], cancellationToken)).Trim();
        if (!string.Equals(head, upstream, StringComparison.OrdinalIgnoreCase))
        {
            _output.WriteLine(
                "main is not what origin/main points at. Publishing from a local commit " +
                "produces a tag nobody else can reproduce.");
            return 1;
        }

        var tags = (await GitAsync(["tag", "--list"], cancellationToken))
            .Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(tag => tag.Trim())
            .ToArray();
        if (tags.Contains(version.ToString(), StringComparer.Ordinal))
        {
            _output.WriteLine($"{version} is already tagged. A published release is not reissued.");
            return 1;
        }

        var notes = ReleaseNotes.Inspect(_repositoryRoot, version);
        if (!notes.ReadyToPublish)
        {
            foreach (var problem in notes.Problems)
            {
                _output.WriteLine(problem);
            }

            _output.WriteLine(
                $"The notes for {version} are not ready. Run this without --publish first.");
            return 1;
        }

        if (!await RequireGreenBuildAsync(head, cancellationToken))
        {
            return 1;
        }

        _output.WriteLine($"Building and signing {version} from {head[..12]}.");
        var build = await RunAsync(
            "pwsh",
            [
                "-NoProfile",
                "-NonInteractive",
                "-File",
                Path.Combine(
                    _repositoryRoot,
                    "scripts",
                    "publish-localai-release-win-x64-self-contained.ps1"),
                "-ReleaseVersion", version.ToString(),
                "-SignManifest",
            ],
            BuildCommand,
            cancellationToken,
            throwOnFailure: false);
        if (build.ExitCode != 0)
        {
            // Both streams, because the reason is usually in neither the one you expect nor both.
            // MSBuild reports a failed build on stdout and PowerShell reports the throw on stderr,
            // and printing only the latter produced a report that said the build failed and not a
            // word about why - a locked file, in the first real run of this command.
            _output.WriteLine(build.StandardOutput.TrimEnd());
            _output.WriteLine(build.StandardError.TrimEnd());
            _output.WriteLine(
                $"The release build failed with code {build.ExitCode?.ToString() ?? "none"}. " +
                "Nothing was published.");
            return 1;
        }

        var releaseDirectory = Path.Combine(_repositoryRoot, "publish", "release");
        var manifestPath = Path.Combine(releaseDirectory, "release-manifest.json");
        var manifest = ReleaseConsistency.ParseManifest(
            await File.ReadAllTextAsync(manifestPath, cancellationToken));
        var problems = ReleaseConsistency.Check(manifest, version, head);
        if (problems.Count > 0)
        {
            foreach (var problem in problems)
            {
                _output.WriteLine(problem);
            }

            _output.WriteLine("Nothing was published.");
            return 1;
        }

        var installer = Path.Combine(
            _repositoryRoot,
            "publish",
            "LocalAi.Installer",
            "LocalAi.Installer.exe");
        var hash = InstallerHash(installer);
        await RunAsync(
            "gh",
            [
                "release", "create", version.ToString(),
                "--target", head,
                "--title", $"LocalAi {version}",
                "--notes-file", await ReleaseBodyAsync(version, hash, cancellationToken),
                Path.Combine(releaseDirectory, "localai-package.zip"),
                Path.Combine(releaseDirectory, "release-manifest.json"),
                Path.Combine(releaseDirectory, "release-manifest.sig"),
                installer,
            ],
            QuickCommand,
            cancellationToken);

        _output.WriteLine($"Published {version} at {head[..12]}.");
        _output.WriteLine($"Installer SHA-256: {hash}");
        return 0;
    }

    /// <summary>
    /// Refuses to release a commit the suite has not gone green on.
    ///
    /// Required checks cannot be enforced by branch protection here — GitHub answers 403 for a
    /// private repository on this plan — so the rule is enforced at the one point where breaking
    /// it is expensive. A red main is a mistake anyone can make; publishing one is a mistake
    /// other people have to install.
    /// </summary>
    private async Task<bool> RequireGreenBuildAsync(
        string commit,
        CancellationToken cancellationToken)
    {
        var runs = await RunAsync(
            "gh",
            [
                "run", "list",
                "--commit", commit,
                "--workflow", "build-and-test.yml",
                "--json", "conclusion,status",
                "--limit", "10",
            ],
            QuickCommand,
            cancellationToken,
            throwOnFailure: false);
        if (runs.ExitCode != 0)
        {
            _output.WriteLine(
                "Could not read the build status for this commit. " +
                $"gh exited with {runs.ExitCode?.ToString() ?? "no code"}.");
            return false;
        }

        using var document = JsonDocument.Parse(
            string.IsNullOrWhiteSpace(runs.StandardOutput) ? "[]" : runs.StandardOutput);
        var green = document.RootElement.EnumerateArray().Any(run =>
            run.TryGetProperty("conclusion", out var conclusion) &&
            string.Equals(conclusion.GetString(), "success", StringComparison.Ordinal));
        if (!green)
        {
            _output.WriteLine(
                $"No successful build-and-test run for {commit[..12]}. " +
                "Wait for CI, or fix it, before publishing.");
        }

        return green;
    }

    private async Task<bool> RequireCleanTreeAsync(
        ReleaseVersion? version,
        CancellationToken cancellationToken)
    {
        // --untracked-files=all, because the default collapses an untracked directory into one
        // entry: a repository where docs/releases does not exist yet reports `?? docs/` and the
        // notes this release is about to commit never appear under their own paths. Asking for
        // the form this can parse is the fix; parsing whichever form git felt like emitting is
        // how the exclusion below silently stops applying.
        var status = await GitAsync(
            ["status", "--porcelain", "--untracked-files=all"],
            cancellationToken);
        var unexpected = UnexpectedChanges(status, version);
        if (unexpected.Count == 0)
        {
            return true;
        }

        _output.WriteLine(
            "The working tree has changes that are not part of this release. A release has to " +
            "be reproducible from the commit it names:");
        foreach (var line in unexpected)
        {
            _output.WriteLine(line);
        }

        return false;
    }

    /// <summary>
    /// The working tree changes that are not this release's own notes.
    ///
    /// Preparing a release writes two files and then wants to commit them, so it cannot also
    /// demand that nothing be written. The first run scaffolded the notes and the second refused
    /// to continue because of the scaffold it had just produced — a command blocked by its own
    /// output, and by a check that was right in spirit.
    ///
    /// Only these two paths, and only for the version being prepared. Notes for some other
    /// version sitting uncommitted are exactly the surprise this check exists to surface.
    /// Publishing passes no version, so nothing is excused there: by then the notes are
    /// committed, and anything left over is a real difference between the tree and the commit
    /// the tag will name.
    /// </summary>
    public static IReadOnlyList<string> UnexpectedChanges(
        string porcelain,
        ReleaseVersion? version)
    {
        ArgumentNullException.ThrowIfNull(porcelain);
        HashSet<string> excused = version is null
            ? new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            : new HashSet<string>(
                [
                    $"docs/releases/{version}.md",
                    $"docs/releases/{version}.ru.md",
                ],
                StringComparer.OrdinalIgnoreCase);
        return porcelain
            .Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(line => line.TrimEnd('\r'))
            .Where(line => !string.IsNullOrWhiteSpace(line))
            .Where(line => !excused.Contains(PorcelainPath(line)))
            .ToArray();
    }

    /// <summary>
    /// The path out of a porcelain line. The first two columns are the status and the third is a
    /// space, and a path containing a space or a quote is quoted — which none of the release note
    /// paths ever are, so an unquoted comparison is enough and a quoted path simply fails to
    /// match and is reported, which is the safe direction.
    /// </summary>
    /// <summary>
    /// The release body: the English notes, with the installer's hash appended.
    ///
    /// The package proves what it is with a signed manifest. The installer that downloads it
    /// proves nothing — it is not Authenticode-signed, so Windows shows the whole
    /// "unrecognised app" screen and gives a reader no way to tell a real download from a
    /// substituted one. A hash on the release page is a weak substitute for a certificate and
    /// a great deal better than nothing, and it belongs where the download is rather than in
    /// the console of whoever ran the release.
    ///
    /// A hash that could not be computed simply leaves the notes alone. A release that is
    /// otherwise ready must not fail over an annotation.
    /// </summary>
    private async Task<string> ReleaseBodyAsync(
        ReleaseVersion version,
        string hash,
        CancellationToken cancellationToken)
    {
        var notes = ReleaseNotes.EnglishPath(_repositoryRoot, version);
        if (hash.StartsWith("unavailable", StringComparison.Ordinal))
        {
            return notes;
        }

        try
        {
            var body = await File.ReadAllTextAsync(notes, cancellationToken)
                .ConfigureAwait(false);
            var path = Path.Combine(
                Path.GetTempPath(),
                $"localai-release-body-{version}.md");
            await File.WriteAllTextAsync(
                    path,
                    body.TrimEnd() + Environment.NewLine + Environment.NewLine +
                    "---" + Environment.NewLine + Environment.NewLine +
                    "`LocalAi.Installer.exe` SHA-256: `" + hash + "`" + Environment.NewLine,
                    cancellationToken)
                .ConfigureAwait(false);
            return path;
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or
                ArgumentException or NotSupportedException)
        {
            return notes;
        }
    }

    /// <summary>
    /// The published installer's hash, or a reason it could not be read. A missing hash must
    /// never fail a release that has already been published — the tag exists by this point.
    /// </summary>
    private static string InstallerHash(string path)
    {
        try
        {
            using var stream = File.OpenRead(path);
            return Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(stream));
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or
                ArgumentException or NotSupportedException)
        {
            return $"unavailable ({exception.Message})";
        }
    }

    private static string PorcelainPath(string line) =>
        line.Length > 3 ? line[3..].Trim() : line;

    private async Task<string> GitAsync(
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken) =>
        (await RunAsync("git", ["-C", _repositoryRoot, .. arguments], QuickCommand, cancellationToken))
            .StandardOutput;

    private async Task<ProcessResult> RunAsync(
        string executable,
        IReadOnlyList<string> arguments,
        TimeSpan timeout,
        CancellationToken cancellationToken,
        bool throwOnFailure = true)
    {
        var result = await _runner.RunAsync(executable, arguments, timeout, cancellationToken);
        if (throwOnFailure && result.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"{executable} {string.Join(' ', arguments)} exited with " +
                $"{result.ExitCode?.ToString() ?? "no code"}: {result.StandardError.Trim()}");
        }

        return result;
    }
}
