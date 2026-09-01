namespace LocalAi.Installer.Core.Tests;

/// <summary>
/// The chosen language is process state — one installer window, one language. Tests that set it
/// therefore cannot run beside tests that read it, and xunit parallelises across classes by
/// default. This collection is what makes "the language is global" survivable in a test run.
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class InstallerLanguageCollection
{
    public const string Name = "installer language";
}
