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
        if (!await RequireCleanTreeAsync(cancellationToken))
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
        if (!await RequireCleanTreeAsync(cancellationToken))
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
            _output.WriteLine(build.StandardError);
            _output.WriteLine($"The release build failed with code {build.ExitCode?.ToString() ?? "none"}.");
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

        await RunAsync(
            "gh",
            [
                "release", "create", version.ToString(),
                "--target", head,
                "--title", $"LocalAi {version}",
                "--notes-file", ReleaseNotes.EnglishPath(_repositoryRoot, version),
                Path.Combine(releaseDirectory, "localai-package.zip"),
                Path.Combine(releaseDirectory, "release-manifest.json"),
                Path.Combine(releaseDirectory, "release-manifest.sig"),
                Path.Combine(_repositoryRoot, "publish", "LocalAi.Installer", "LocalAi.Installer.exe"),
            ],
            QuickCommand,
            cancellationToken);

        _output.WriteLine($"Published {version} at {head[..12]}.");
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

    private async Task<bool> RequireCleanTreeAsync(CancellationToken cancellationToken)
    {
        var status = await GitAsync(["status", "--porcelain"], cancellationToken);
        if (string.IsNullOrWhiteSpace(status))
        {
            return true;
        }

        _output.WriteLine(
            "The working tree has uncommitted changes. A release has to be reproducible from " +
            "the commit it names:");
        _output.WriteLine(status.TrimEnd());
        return false;
    }

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
