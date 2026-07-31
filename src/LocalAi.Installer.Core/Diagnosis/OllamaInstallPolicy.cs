namespace LocalAi.Installer.Core.Diagnosis;

internal sealed class OllamaInstallPolicy
{
    private readonly OllamaInstallPathPolicy _paths;

    public OllamaInstallPolicy(
        IPhysicalPathResolver physicalPathResolver,
        IEnumerable<string> approvedDirectories)
    {
        _paths = new OllamaInstallPathPolicy(
            physicalPathResolver,
            approvedDirectories);
    }

    public InstalledApplicationMetadata? Match(
        UninstallEntrySnapshot entry,
        IExecutableIdentityProbe executableIdentity)
    {
        if (!OllamaVersionPolicy.TryValidateDisplayName(
                entry.DisplayName,
                out var displayNameVersion))
        {
            return null;
        }

        var candidateDirectories = _paths.GetCandidateDirectories(entry);
        foreach (var candidate in candidateDirectories.Select(
                     directory => Path.Combine(directory, "ollama.exe")))
        {
            var identity = executableIdentity.Inspect(candidate);
            if (!_paths.IsCandidatePhysicalPath(
                    candidate,
                    identity.Path,
                    candidateDirectories) ||
                !IsTrustedOllamaIdentity(identity))
            {
                continue;
            }

            if (!_paths.IsApprovedOfficialPhysicalPath(identity.Path) &&
                !HasStrongOllamaIdentity(identity))
            {
                continue;
            }

            if (!OllamaVersionPolicy.TryResolveConsistentVersion(
                    entry.DisplayVersion,
                    identity.FileVersion,
                    displayNameVersion,
                    out var detectedVersion))
            {
                continue;
            }

            return new InstalledApplicationMetadata(
                entry.DisplayName!,
                entry.DisplayVersion,
                entry.InstallLocation,
                Path.GetFullPath(identity.Path),
                identity.FileVersion,
                detectedVersion);
        }

        return null;
    }

    private static bool IsTrustedOllamaIdentity(
        ExecutableIdentitySnapshot identity)
    {
        if (!identity.Exists ||
            !string.Equals(
                Path.GetFileName(identity.Path),
                "ollama.exe",
                StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (!string.IsNullOrWhiteSpace(identity.ProductName) &&
            !string.Equals(
                identity.ProductName.Trim(),
                "Ollama",
                StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return string.IsNullOrWhiteSpace(identity.OriginalFileName) ||
               string.Equals(
                   identity.OriginalFileName.Trim(),
                   "ollama.exe",
                   StringComparison.OrdinalIgnoreCase);
    }

    private static bool HasStrongOllamaIdentity(
        ExecutableIdentitySnapshot identity) =>
        string.Equals(
            identity.ProductName?.Trim(),
            "Ollama",
            StringComparison.OrdinalIgnoreCase) &&
        string.Equals(
            identity.OriginalFileName?.Trim(),
            "ollama.exe",
            StringComparison.OrdinalIgnoreCase);
}
