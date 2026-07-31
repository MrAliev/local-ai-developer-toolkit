namespace LocalAi.Installer.Core.Activation;

public sealed class InstallationLayout
{
    private static readonly HashSet<string> ReservedNames = new(
        [
            "CON", "PRN", "AUX", "NUL", "CLOCK$", "CONIN$", "CONOUT$",
            "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
            "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
        ],
        StringComparer.OrdinalIgnoreCase);

    private InstallationLayout(string localAppData)
    {
        LocalAppData = CanonicalizeLocalAppData(localAppData);
        Root = Path.Combine(LocalAppData, "LocalAi");
        BinRoot = Path.Combine(Root, "bin");
        VersionsRoot = Path.Combine(BinRoot, "versions");
        LauncherDirectory = Path.Combine(BinRoot, "launcher");
        LauncherPath = Path.Combine(LauncherDirectory, LocalAi.Contracts.LocalAiPackageLayout.StableLauncherFile);
        CurrentPointerPath = Path.Combine(BinRoot, "current.json");
        InstallerDirectory = Path.Combine(Root, "installer");
        InstallerBackupsRoot = Path.Combine(InstallerDirectory, "backups");
    }

    public string LocalAppData { get; }
    public string Root { get; }
    public string BinRoot { get; }
    public string VersionsRoot { get; }
    public string LauncherDirectory { get; }
    public string LauncherPath { get; }
    public string CurrentPointerPath { get; }
    public string InstallerDirectory { get; }
    public string InstallerBackupsRoot { get; }

    public static InstallationLayout CreateDefault()
    {
        var value = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new InstallationLayoutException("The LocalApplicationData directory is unavailable.");
        }

        return FromLocalAppData(value);
    }

    public static InstallationLayout FromLocalAppData(string localAppData) => new(localAppData);

    private static string CanonicalizeLocalAppData(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || !Path.IsPathFullyQualified(value))
        {
            throw new InstallationLayoutException("The installation root is unsafe.");
        }

        try
        {
            var canonical = Path.TrimEndingDirectorySeparator(Path.GetFullPath(value));
            var supplied = Path.TrimEndingDirectorySeparator(value);
            var comparison = OperatingSystem.IsWindows()
                ? StringComparison.OrdinalIgnoreCase
                : StringComparison.Ordinal;
            if (!string.Equals(supplied, canonical, comparison))
            {
                throw new InstallationLayoutException("The installation root is not canonical.");
            }

            var root = Path.TrimEndingDirectorySeparator(Path.GetPathRoot(canonical)!);
            if (string.Equals(canonical, root, StringComparison.OrdinalIgnoreCase))
            {
                throw new InstallationLayoutException("The installation root is unsafe.");
            }

            foreach (var segment in canonical[root.Length..].Split(
                         Path.DirectorySeparatorChar,
                         StringSplitOptions.RemoveEmptyEntries))
            {
                var stem = segment.Split('.')[0];
                if (segment.EndsWith(' ') || segment.EndsWith('.') ||
                    segment.Contains(':') || ReservedNames.Contains(stem))
                {
                    throw new InstallationLayoutException("The installation root is unsafe.");
                }
            }

            var ancestor = Path.GetPathRoot(canonical)!;
            foreach (var segment in canonical[root.Length..].Split(
                         Path.DirectorySeparatorChar,
                         StringSplitOptions.RemoveEmptyEntries))
            {
                ancestor = Path.Combine(ancestor, segment);
                if ((Directory.Exists(ancestor) || File.Exists(ancestor)) &&
                    File.GetAttributes(ancestor).HasFlag(FileAttributes.ReparsePoint))
                {
                    throw new InstallationLayoutException("The installation root has a reparse ancestor.");
                }
            }

            return canonical;
        }
        catch (Exception exception) when (
            exception is ArgumentException or NotSupportedException or PathTooLongException or
            IOException or UnauthorizedAccessException)
        {
            throw new InstallationLayoutException("The installation root is unsafe.");
        }
    }
}

public sealed class InstallationLayoutException(string message) : Exception(message);
