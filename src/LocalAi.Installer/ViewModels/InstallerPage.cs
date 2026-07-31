using System.Globalization;

namespace LocalAi.Installer.ViewModels;

public enum InstallerPage
{
    Diagnose,
    Dependencies,
    Package,
    Models,
    Agents,
    ReviewApply,
    Finish,
}

public enum ModelSelectionMode
{
    Automatic,
    Manual,
    Skip,
}

public enum AgentChoice
{
    None,
    Skip,
    ConfigureExisting,
    InstallManagedBlock,
    RunWithoutAgent,
}

public sealed record DependencySelection(string Id, string Title, bool IsRequired)
{
    public bool IsConsented { get; set; }
    public bool IsInstalled { get; set; }
}

public sealed record RecommendedModel(string Id, string Tier)
{
}

public sealed record AgentOption(string Agent, AgentChoice Choice)
{
}

public static class InstallerCulture
{
    public static string CurrentCultureCode { get; set; } = CultureInfo.CurrentUICulture.Name;
}
