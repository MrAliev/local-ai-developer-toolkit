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

        var trustedDirectories = _paths.GetTrustedDirectories(entry);
        foreach (var candidate in trustedDirectories.Select(
                     directory => Path.Combine(directory, "ollama.exe")))
        {
            var identity = executableIdentity.Inspect(candidate);
            if (!IsCompatibleOllamaMetadata(identity) ||
                !_paths.IsTrustedPhysicalPath(
                    candidate,
                    identity.Path,
                    trustedDirectories))
            {
                continue;
            }

            var detectedVersion =
                OllamaVersionPolicy.Validate(entry.DisplayVersion) ??
                OllamaVersionPolicy.Validate(identity.FileVersion) ??
                displayNameVersion;
            if (detectedVersion is null)
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

    private static bool IsCompatibleOllamaMetadata(
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
}
