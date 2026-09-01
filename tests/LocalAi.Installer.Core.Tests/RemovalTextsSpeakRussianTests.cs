using System.Text.RegularExpressions;
using LocalAi.Installer.Core.Removal;

namespace LocalAi.Installer.Core.Tests;

/// <summary>
/// The removal wizard cannot be translated from the WPF project alone: the rows a person ticks,
/// the presets above them and the preview they consent to are all built here, in the core. A
/// half-translated consent surface — Russian frame, English rows — is worse than either whole,
/// because the sentence somebody agrees to is the one they cannot read.
/// </summary>
[Collection(InstallerLanguageCollection.Name)]
public sealed class RemovalTextsSpeakRussianTests : IDisposable
{
    private readonly InstallerLanguage original = InstallerCulture.Current;

    public void Dispose() => InstallerCulture.Current = original;

    /// <summary>
    /// Latin letters are allowed — every one of these lines carries a product name, a command or
    /// a path. Cyrillic ones are what a translation adds; a row without any is still English.
    /// </summary>
    [Fact]
    public void Every_row_names_itself_in_russian()
    {
        InstallerCulture.Current = InstallerLanguage.Russian;

        foreach (var item in RemovalMatrix.Items)
        {
            AssertRussian(RemovalMatrix.Title(item), $"Title({item})");
            AssertRussian(RemovalMatrix.Note(item), $"Note({item})");
        }
    }

    [Fact]
    public void Every_preset_names_itself_in_russian()
    {
        InstallerCulture.Current = InstallerLanguage.Russian;

        foreach (var preset in RemovalMatrix.Presets)
        {
            AssertRussian(RemovalMatrix.Title(preset), $"Title({preset})");
            AssertRussian(RemovalMatrix.Description(preset), $"Description({preset})");
        }
    }

    /// <summary>
    /// The English is the one everybody has been reading; a translation that quietly reworded it
    /// would be a second change hidden inside the first.
    /// </summary>
    [Fact]
    public void The_english_rows_are_left_exactly_as_they_were()
    {
        InstallerCulture.Current = InstallerLanguage.English;

        Assert.Equal("Binaries, launcher, version pointer", RemovalMatrix.Title(RemovalItem.Binaries));
        Assert.Equal("Removing these is the uninstall.", RemovalMatrix.Note(RemovalItem.Binaries));
        Assert.Equal("Full uninstall", RemovalMatrix.Title(RemovalPreset.FullUninstall));
        Assert.Equal(
            "The binaries go; the indexes and settings a reinstall would honour stay.",
            RemovalMatrix.Description(RemovalPreset.ReinstallFriendly));
    }

    private static void AssertRussian(string text, string what)
    {
        Assert.False(string.IsNullOrWhiteSpace(text), $"{what} is empty");
        Assert.True(
            Regex.IsMatch(text, "[а-яА-ЯёЁ]"),
            $"{what} is still English: {text}");
    }
}
