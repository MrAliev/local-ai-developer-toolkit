using System.Text.Json;
using LocalAi.Contracts;
using LocalAi.Contracts.Activation;

namespace CodeSearch.Mcp;

/// <summary>
/// The one line an agent is told about a newer release, and the many cases where it is told
/// nothing.
///
/// It rides on `index_status` because that is the status an agent reads routinely; putting it
/// on every tool would make it noise, and putting it nowhere would make the check pointless.
/// It is read from the state file the broker writes — this never touches the network — and it
/// stays outside the untrusted-content boundary, because it is this installation talking about
/// itself rather than anything that came out of a repository.
/// </summary>
public static class UpdateNotice
{
    /// <summary>
    /// The notice, or an empty string. Empty covers every case except one: the check is
    /// switched on, the last answer verified, and the version it names is newer than the one
    /// installed. A person who never asked for release lookups never sees one mentioned.
    /// </summary>
    public static string ForStatus(string? runtimeRoot)
    {
        try
        {
            var root = string.IsNullOrWhiteSpace(runtimeRoot)
                ? ModelResidencyPolicyStore.DefaultRuntimeRoot
                : runtimeRoot;
            if (!new UpdateCheckPolicyStore(root).Read().Enabled)
            {
                return string.Empty;
            }

            var state = new UpdateCheckStateStore(root).Read();
            var installed = InstalledVersionReader.Read(root);
            return UpdateComparison.Compare(state, installed) == UpdateAvailability.Available
                ? $"\nUpdate:     LocalAi {state.LatestVersion} is available " +
                    $"(this installation is {installed.DisplayName}). {state.ReleaseUrl}"
                : string.Empty;
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or ArgumentException or
                JsonException)
        {
            // A status that cannot read a small optional file still answers what it was asked.
            return string.Empty;
        }
    }
}
