namespace LocalAi.Contracts;

/// <summary>
/// Where LocalAi keeps what it is given, as opposed to what it builds.
///
/// Everything used to live loose in the runtime root: binaries, indexes, the queue, telemetry
/// and settings in one directory, told apart only by a list of file names kept somewhere else.
/// That list fell behind — <c>semantic-navigation.json</c> is a setting the removal matrix
/// classified as an unrecognised runtime file, so a reinstall that promised to keep settings
/// deleted it — and it was always going to, because nothing made adding a setting and adding
/// its name to the list the same act.
///
/// A directory cannot fall behind. A file under <see cref="Settings"/> is a setting because of
/// where it is, and the next one added is a setting without anybody remembering anything.
///
/// The split between the two roots is about what a setting is attached to. Residency policy is
/// a statement about this machine's graphics card; indexing limits are about its memory.
/// Carrying those to another machine would be carrying a wrong answer, so they stay in
/// LocalAppData, which does not roam. What a person chooses for themselves rather than for a
/// machine belongs in <see cref="UserData"/>, which does.
/// </summary>
public static class RuntimeDirectories
{
    /// <summary>The directory name settings live in, under the runtime root.</summary>
    public const string SettingsDirectoryName = "settings";

    /// <summary>The product's directory name, under either application-data root.</summary>
    public const string ProductDirectoryName = "LocalAi";

    /// <summary>
    /// Machine-bound settings: <c>%LOCALAPPDATA%\LocalAi\settings</c>. Tied to this computer's
    /// hardware and this installation, and deliberately not roamed.
    /// </summary>
    public static string Settings(string runtimeRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(runtimeRoot);
        return Path.Combine(runtimeRoot, SettingsDirectoryName);
    }

    /// <summary>
    /// The path a settings file is read from and written to, and the legacy path it may still
    /// be at. Reading falls back to the legacy location so an installation that predates the
    /// split keeps answering; writing only ever goes to the new one, so the fallback empties
    /// itself over time rather than becoming a second source of truth.
    /// </summary>
    public static string SettingsFile(string runtimeRoot, string fileName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fileName);
        var current = Path.Combine(Settings(runtimeRoot), fileName);
        if (File.Exists(current))
        {
            return current;
        }

        var legacy = Path.Combine(runtimeRoot, fileName);
        return File.Exists(legacy) ? legacy : current;
    }

    /// <summary>Where a settings file is written, regardless of where it was read from.</summary>
    public static string SettingsFileForWriting(string runtimeRoot, string fileName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fileName);
        return Path.Combine(Settings(runtimeRoot), fileName);
    }

    /// <summary>
    /// Takes the legacy copy away once the setting has been written to its new home.
    ///
    /// Without this the old file is not a fallback but a second copy that never goes away:
    /// it holds whatever it held before the split, forever, on every upgraded machine. That
    /// is worse than it sounds, because everything that still builds the legacy path by hand
    /// — a doctor check, a journal entry, a line in the README — keeps finding it and keeps
    /// describing it, while the runtime reads the other one.
    ///
    /// Called after the write, so a failed write leaves the old copy where it was and the
    /// installation keeps answering from it.
    /// </summary>
    public static void DiscardLegacySettingsFile(string runtimeRoot, string fileName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fileName);
        var legacy = Path.Combine(runtimeRoot, fileName);
        var current = SettingsFileForWriting(runtimeRoot, fileName);
        if (!File.Exists(legacy) || !File.Exists(current))
        {
            return;
        }

        try
        {
            File.Delete(legacy);
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException)
        {
            // A locked or read-only legacy file is not worth failing a write over: the new
            // one is already in place and is what will be read.
        }
    }

    /// <summary>
    /// What the person keeps rather than the machine: <c>%APPDATA%\LocalAi</c>. Roams with the
    /// profile, survives an uninstall of the runtime, and holds nothing that could be wrong on
    /// another computer.
    /// </summary>
    public static string UserData => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        ProductDirectoryName);
}
