using LocalAi.Contracts;
using LocalAi.Installer.Core.Removal;

namespace LocalAi.Installer.Core.Tests;

/// <summary>
/// Settings are told apart from everything else by the directory they are in.
///
/// They used to be told apart by a list of file names kept in the removal matrix, and the list
/// fell behind: <c>semantic-navigation.json</c> is a real setting that the matrix classified as
/// an unrecognised runtime file, so the reinstall-friendly preset — which promises to keep the
/// settings a reinstall would honour — deleted it. Nothing made adding a setting and adding its
/// name to that list the same act, so the two drifted, and would have drifted again.
/// </summary>
public sealed class SettingsDirectoryTests
{
    /// <summary>
    /// The point of the change: a setting nobody has heard of is still a setting. This is the
    /// test the old design could not have.
    /// </summary>
    [Fact]
    public void A_setting_invented_tomorrow_is_classified_without_anybody_being_told()
    {
        Assert.Equal(
            RemovalItem.Settings,
            RemovalMatrix.ClassifyRootEntry(RuntimeDirectories.SettingsDirectoryName));
    }

    /// <summary>
    /// And the preset that promises to keep settings keeps that directory, so the promise now
    /// covers settings that did not exist when the promise was written.
    /// </summary>
    [Fact]
    public void A_reinstall_keeps_the_settings_directory()
    {
        var selection = RemovalSelection.FromPreset(RemovalPreset.ReinstallFriendly);

        Assert.False(selection.Includes(RemovalItem.Settings));
        Assert.Equal(
            RemovalItem.Settings,
            RemovalMatrix.ClassifyRootEntry(RuntimeDirectories.SettingsDirectoryName));
    }

    /// <summary>
    /// The setting the old list missed. Installations that predate the settings directory
    /// still have it loose in the runtime root, so the name has to stay recognised — this is
    /// the defect that prompted the change, pinned so a tidy-up cannot reintroduce it.
    /// </summary>
    [Fact]
    public void The_setting_the_list_had_missed_is_recognised_where_it_still_lies()
    {
        Assert.Equal(
            RemovalItem.Settings,
            RemovalMatrix.ClassifyRootEntry("semantic-navigation.json"));
        Assert.Contains("semantic-navigation.json", RemovalMatrix.SettingsFileNames);
    }

    /// <summary>
    /// Reading falls back to the legacy location, writing never does — otherwise the two
    /// places become two answers, and which one wins depends on which component wrote last.
    /// </summary>
    [Fact]
    public void Reading_falls_back_to_the_old_place_and_writing_never_does()
    {
        var root = Path.Combine(Path.GetTempPath(), "LocalAi.Settings", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var legacy = Path.Combine(root, "policy.json");
            File.WriteAllText(legacy, "{}");

            Assert.Equal(legacy, RuntimeDirectories.SettingsFile(root, "policy.json"));
            Assert.Equal(
                Path.Combine(root, RuntimeDirectories.SettingsDirectoryName, "policy.json"),
                RuntimeDirectories.SettingsFileForWriting(root, "policy.json"));

            // Once it has been written to the new place, that is what is read.
            var current = RuntimeDirectories.SettingsFileForWriting(root, "policy.json");
            Directory.CreateDirectory(Path.GetDirectoryName(current)!);
            File.WriteAllText(current, "{}");
            Assert.Equal(current, RuntimeDirectories.SettingsFile(root, "policy.json"));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    /// <summary>
    /// A setting neither file exists for resolves to where it would be written, so a caller
    /// asking for a path it will create does not get the legacy one.
    /// </summary>
    [Fact]
    public void A_setting_that_does_not_exist_yet_resolves_to_the_new_place()
    {
        var root = Path.Combine(Path.GetTempPath(), "LocalAi.Settings", Guid.NewGuid().ToString("N"));

        Assert.Equal(
            RuntimeDirectories.SettingsFileForWriting(root, "policy.json"),
            RuntimeDirectories.SettingsFile(root, "policy.json"));
    }

    /// <summary>
    /// What the person keeps rather than the machine roams with the profile, and is therefore
    /// not under the runtime root at all — an uninstall of the runtime does not take it, and a
    /// residency policy chosen for one machine's graphics card cannot follow them to another.
    /// </summary>
    [Fact]
    public void User_data_is_outside_the_runtime_root()
    {
        Assert.StartsWith(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            RuntimeDirectories.UserData,
            StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            RuntimeDirectories.UserData,
            StringComparison.OrdinalIgnoreCase);
    }
}
