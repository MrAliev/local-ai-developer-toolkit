using LocalAi.Contracts;

namespace LocalAi.Installer.Core.Activation;

public enum ResidencyPolicyOutcome
{
    Applied,

    /// <summary>
    /// Nothing was written, because there is no installation to write it into.
    /// </summary>
    SkippedWithoutInstallation,
}

/// <summary>
/// Stores the residency choice — and refuses to be the thing that creates the LocalAi root.
///
/// <see cref="ModelResidencyPolicyStore.Write"/> creates its parent with a plain
/// <c>Directory.CreateDirectory</c>, which inherits whatever %LOCALAPPDATA% grants. The
/// installation layout lease requires the root to carry a protected ACL and is deliberately
/// forbidden from mutating one, so a root created that way is refused for good:
///
///   The LocalAi installation layout is unsafe (check: ValidateAcl): the directory still
///   inherits access rules.
///
/// The wizard used to apply the policy unconditionally at the end of a run. Ordering it after
/// the package install was believed to close that gap, and it does — but only when the package
/// actually installs. A first run that fails at the package step (no GitHub sign-in, no
/// network, a cancelled download) still reached this code, created the root by hand, and left
/// the machine in a state where every later installation refuses itself, with a message that
/// names the condition but not the cure. That is the worst kind of failure: caused by the
/// installer, blamed on the user, and fixable only by deleting a directory nobody mentioned.
///
/// So the write is conditional on an installation already being there. A machine with LocalAi
/// installed can still change its residency without reinstalling; a machine without it is left
/// exactly as it was found.
/// </summary>
public static class ResidencyPolicyWriter
{
    public static ResidencyPolicyOutcome Apply(
        string runtimeRoot,
        ModelResidencyPolicy residency)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(runtimeRoot);
        if (!Directory.Exists(runtimeRoot))
        {
            return ResidencyPolicyOutcome.SkippedWithoutInstallation;
        }

        var store = new ModelResidencyPolicyStore(runtimeRoot);
        store.Write(store.Read() with { ModelResidency = residency });
        return ResidencyPolicyOutcome.Applied;
    }
}
