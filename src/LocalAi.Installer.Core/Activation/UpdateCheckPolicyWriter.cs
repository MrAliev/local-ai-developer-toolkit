using LocalAi.Contracts;

namespace LocalAi.Installer.Core.Activation;

/// <summary>
/// Stores the answer to the update-check question, under the same rule the residency policy
/// follows: never be the thing that creates the LocalAi root.
///
/// A store's <c>Write</c> creates its parent with a plain <c>Directory.CreateDirectory</c>,
/// which inherits whatever %LOCALAPPDATA% grants, and the installation layout lease refuses a
/// root that carries inherited access rules — permanently, with a message that names the
/// condition and not the cure. A first run that fails at the package step must not leave that
/// behind, so this writes only into an installation that already exists.
///
/// Saying no is a write too. Somebody who unticks the box on a reinstall is changing a setting
/// that may already say yes, and skipping the write because the answer was "no" would leave the
/// old yes in place.
/// </summary>
public static class UpdateCheckPolicyWriter
{
    public static ResidencyPolicyOutcome Apply(string runtimeRoot, bool enabled)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(runtimeRoot);
        if (!Directory.Exists(runtimeRoot))
        {
            return ResidencyPolicyOutcome.SkippedWithoutInstallation;
        }

        var store = new UpdateCheckPolicyStore(runtimeRoot);
        store.Write(store.Read() with { Enabled = enabled });
        return ResidencyPolicyOutcome.Applied;
    }
}
