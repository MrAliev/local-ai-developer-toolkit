using LocalAi.Installer.Core.Abstractions;

namespace LocalAi.Installer.Core.Diagnosis;

public sealed class WindowsInstalledApplicationProbe : IInstalledApplicationProbe
{
    private readonly IUninstallEntrySource _entrySource;
    private readonly IExecutableIdentityProbe _executableIdentity;

    public WindowsInstalledApplicationProbe()
        : this(
            new WindowsRegistryUninstallEntrySource(),
            new SystemExecutableIdentityProbe())
    {
    }

    public WindowsInstalledApplicationProbe(
        IUninstallEntrySource entrySource,
        IExecutableIdentityProbe executableIdentity)
    {
        _entrySource = entrySource
            ?? throw new ArgumentNullException(nameof(entrySource));
        _executableIdentity = executableIdentity
            ?? throw new ArgumentNullException(nameof(executableIdentity));
    }

    public Task<InstalledApplicationMetadata?> FindOllamaAsync(
        CancellationToken cancellationToken)
    {
        foreach (var entry in _entrySource.ReadEntries(cancellationToken))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!string.Equals(
                    entry.DisplayName,
                    "Ollama",
                    StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            foreach (var candidate in ResolveExecutableCandidates(entry))
            {
                var identity = _executableIdentity.Inspect(candidate);
                if (!IsApprovedOllamaIdentity(identity))
                {
                    continue;
                }

                return Task.FromResult<InstalledApplicationMetadata?>(
                    new InstalledApplicationMetadata(
                        entry.DisplayName!,
                        entry.DisplayVersion,
                        entry.InstallLocation,
                        identity.Path,
                        identity.FileVersion));
            }
        }

        return Task.FromResult<InstalledApplicationMetadata?>(null);
    }

    private static IEnumerable<string> ResolveExecutableCandidates(
        UninstallEntrySnapshot entry)
    {
        var candidates = new List<string>();
        AddCandidate(candidates, ParseDisplayIcon(entry.DisplayIcon));
        if (!string.IsNullOrWhiteSpace(entry.InstallLocation))
        {
            AddCandidate(
                candidates,
                Path.Combine(entry.InstallLocation, "ollama.exe"));
        }

        AddCandidate(
            candidates,
            ParseExecutableFromCommand(entry.UninstallString));
        return candidates;
    }

    private static void AddCandidate(List<string> candidates, string? candidate)
    {
        if (string.IsNullOrWhiteSpace(candidate) ||
            !string.Equals(
                Path.GetFileName(candidate),
                "ollama.exe",
                StringComparison.OrdinalIgnoreCase) ||
            candidates.Contains(candidate, StringComparer.OrdinalIgnoreCase))
        {
            return;
        }

        candidates.Add(candidate);
    }

    private static bool IsApprovedOllamaIdentity(
        ExecutableIdentitySnapshot identity) =>
        identity.Exists &&
        string.Equals(
            Path.GetFileName(identity.Path),
            "ollama.exe",
            StringComparison.OrdinalIgnoreCase) &&
        !string.IsNullOrWhiteSpace(identity.FileVersion) &&
        (string.Equals(
             identity.ProductName,
             "Ollama",
             StringComparison.OrdinalIgnoreCase) ||
         string.Equals(
             identity.OriginalFileName,
             "ollama.exe",
             StringComparison.OrdinalIgnoreCase));

    private static string? ParseDisplayIcon(string? displayIcon)
    {
        if (string.IsNullOrWhiteSpace(displayIcon))
        {
            return null;
        }

        var path = displayIcon.Trim().Trim('"');
        var comma = path.LastIndexOf(',');
        if (comma > 0 && int.TryParse(path[(comma + 1)..], out _))
        {
            path = path[..comma].Trim().Trim('"');
        }

        return path;
    }

    private static string? ParseExecutableFromCommand(string? command)
    {
        if (string.IsNullOrWhiteSpace(command))
        {
            return null;
        }

        var trimmed = command.Trim();
        if (trimmed[0] == '"')
        {
            var closingQuote = trimmed.IndexOf('"', 1);
            return closingQuote > 1
                ? trimmed[1..closingQuote]
                : null;
        }

        var firstSpace = trimmed.IndexOf(' ');
        return firstSpace < 0
            ? trimmed
            : trimmed[..firstSpace];
    }
}
