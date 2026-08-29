using LocalAi.Installer.Core.Dependencies;

namespace LocalAi.Installer.Core.Tests;

/// <summary>
/// The wizard used to run whatever the environment detector reported, and the detector reports
/// the first file named winget.exe on a search path the user can write to. It then used that to
/// install software machine-wide.
///
/// What this guards is not only that a bad winget is refused, but that the refusal is closed and
/// that the check is repeated: detection happens on the first page and the last dependency is
/// installed several pages later, so a single check would only prove what was true then.
/// </summary>
public sealed class TrustedWingetSourceTests
{
    private const string Detected = @"C:\Users\someone\AppData\Local\Microsoft\WindowsApps\winget.exe";
    private const string Canonical =
        @"C:\Program Files\WindowsApps\Microsoft.DesktopAppInstaller_1.0.0.0_x64__8wekyb3d8bbwe\winget.exe";

    [Fact]
    public void A_trusted_winget_is_run_from_the_path_the_check_resolved()
    {
        var trust = new ScriptedTrust(Trusted());

        var authorized = new TrustedWingetSource(trust).Authorize(Detected);

        Assert.True(authorized.Allowed);
        // Not the detected path: that one only located a candidate, and locating is not trusting.
        Assert.Equal(Canonical, authorized.ExecutablePath);
        Assert.Equal([Detected], trust.Resolved);
        Assert.Empty(trust.Revalidated);
    }

    /// <summary>
    /// The second install is where a planted binary would be swapped in, because by then the
    /// first one has already been approved.
    /// </summary>
    [Fact]
    public void Every_later_use_revalidates_rather_than_trusting_the_first_answer()
    {
        var trust = new ScriptedTrust(Trusted(), Trusted(), Trusted());
        var source = new TrustedWingetSource(trust);

        source.Authorize(Detected);
        source.Authorize(Detected);
        source.Authorize(Detected);

        Assert.Single(trust.Resolved);
        Assert.Equal(2, trust.Revalidated.Count);
        Assert.All(trust.Revalidated, executable => Assert.Equal(Canonical, executable.CanonicalPath));
    }

    [Fact]
    public void A_winget_that_changed_after_it_was_approved_is_refused()
    {
        var trust = new ScriptedTrust(
            Trusted(),
            new ExecutableTrustResult(ExecutableTrustStatus.Changed, null));
        var source = new TrustedWingetSource(trust);
        source.Authorize(Detected);

        var authorized = source.Authorize(Detected);

        Assert.False(authorized.Allowed);
        Assert.Equal(ExecutableTrustStatus.Changed, authorized.Status);
        // Closed: there is no path to run, so a caller cannot carry on regardless.
        Assert.Equal(string.Empty, authorized.ExecutablePath);
    }

    /// <summary>
    /// Revalidating against something already found wanting would keep asking about the wrong
    /// file. After a refusal the next attempt starts over.
    /// </summary>
    [Fact]
    public void A_refusal_forgets_what_was_approved_before_it()
    {
        var trust = new ScriptedTrust(
            Trusted(),
            new ExecutableTrustResult(ExecutableTrustStatus.Changed, null),
            Trusted());
        var source = new TrustedWingetSource(trust);

        source.Authorize(Detected);
        source.Authorize(Detected);
        source.Authorize(Detected);

        Assert.Equal([Detected, Detected], trust.Resolved);
        Assert.Single(trust.Revalidated);
    }

    [Theory]
    [InlineData(ExecutableTrustStatus.InvalidPath)]
    [InlineData(ExecutableTrustStatus.Unavailable)]
    [InlineData(ExecutableTrustStatus.UntrustedPublisher)]
    [InlineData(ExecutableTrustStatus.UntrustedAcl)]
    [InlineData(ExecutableTrustStatus.Changed)]
    [InlineData(ExecutableTrustStatus.UnsupportedPlatform)]
    public void Every_refusal_is_closed_and_carries_a_reason(ExecutableTrustStatus status)
    {
        var trust = new ScriptedTrust(new ExecutableTrustResult(status, null));

        var authorized = new TrustedWingetSource(trust).Authorize(Detected);

        Assert.False(authorized.Allowed);
        Assert.Equal(string.Empty, authorized.ExecutablePath);
        Assert.Equal(status, authorized.Status);
        Assert.NotEmpty(authorized.Message);
    }

    /// <summary>
    /// A person reading this has to know what to do next; "verification failed" leaves them
    /// with nothing to act on.
    /// </summary>
    [Theory]
    [InlineData(ExecutableTrustStatus.InvalidPath)]
    [InlineData(ExecutableTrustStatus.Unavailable)]
    [InlineData(ExecutableTrustStatus.UntrustedPublisher)]
    [InlineData(ExecutableTrustStatus.UntrustedAcl)]
    [InlineData(ExecutableTrustStatus.Changed)]
    public void A_refusal_says_what_to_do_about_it(ExecutableTrustStatus status)
    {
        var message = TrustedWingetSource.Explain(status);

        Assert.Contains(
            new[] { "Microsoft Store", "start it again" },
            hint => message.Contains(hint, StringComparison.Ordinal));
    }

    [Fact]
    public void Distinct_causes_do_not_share_one_message()
    {
        var messages = new[]
        {
            ExecutableTrustStatus.InvalidPath,
            ExecutableTrustStatus.Unavailable,
            ExecutableTrustStatus.UntrustedPublisher,
            ExecutableTrustStatus.UntrustedAcl,
            ExecutableTrustStatus.Changed,
        }.Select(TrustedWingetSource.Explain).ToArray();

        Assert.Equal(messages.Length, messages.Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public void A_detector_that_found_nothing_is_still_asked_about_rather_than_assumed_bad()
    {
        var trust = new ScriptedTrust(
            new ExecutableTrustResult(ExecutableTrustStatus.InvalidPath, null));

        var authorized = new TrustedWingetSource(trust).Authorize(null);

        Assert.False(authorized.Allowed);
        // Empty rather than null: the check owns the decision, so it is the one that says no.
        Assert.Equal([string.Empty], trust.Resolved);
    }

    private static ExecutableTrustResult Trusted() =>
        new(
            ExecutableTrustStatus.Trusted,
            new TrustedExecutable(
                Canonical,
                "Microsoft.DesktopAppInstaller",
                "Microsoft Corporation",
                "Microsoft.DesktopAppInstaller_1.0.0.0_x64__8wekyb3d8bbwe",
                @"C:\Program Files\WindowsApps\Microsoft.DesktopAppInstaller_1.0.0.0_x64__8wekyb3d8bbwe"));

    private sealed class ScriptedTrust(params ExecutableTrustResult[] results) : IWinGetExecutableTrust
    {
        private readonly Queue<ExecutableTrustResult> results = new(results);

        public List<string> Resolved { get; } = [];

        public List<TrustedExecutable> Revalidated { get; } = [];

        public ExecutableTrustResult Resolve(string snapshotPath)
        {
            Resolved.Add(snapshotPath);
            return results.Dequeue();
        }

        public ExecutableTrustResult Revalidate(TrustedExecutable executable)
        {
            Revalidated.Add(executable);
            return results.Dequeue();
        }
    }
}
