namespace LocalAi.Installer.Tests;

/// <summary>
/// The installer's language is process state — one window at a time, set once at startup — so
/// the classes that read or set it cannot run beside each other. Without this they share a
/// static across threads and a class asserting English reads the Russian a neighbour just
/// chose, which is how these first failed: not flakily, but on whichever order the runner took.
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class InstallerLanguageCollection
{
    public const string Name = "installer language";
}
