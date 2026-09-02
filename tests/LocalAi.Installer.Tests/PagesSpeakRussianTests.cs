using LocalAi.Installer.Core;
using LocalAi.Installer.ViewModels;

namespace LocalAi.Installer.Tests;

/// <summary>
/// The rows inside each page: what the check table says, what a prerequisite costs to skip, and
/// what each client integration would do. These are the lines somebody reads before ticking a
/// box, so a page whose heading is Russian and whose rows are English asks for consent to a
/// sentence the reader was told they would not have to parse.
/// </summary>
public sealed class PagesSpeakRussianTests : IDisposable
{
    private readonly InstallerLanguage original = InstallerCulture.Current;

    public void Dispose() => InstallerCulture.Current = original;

    [Fact]
    public void The_check_table_reports_its_verdicts_in_russian()
    {
        InstallerCulture.Current = InstallerLanguage.Russian;

        foreach (var status in Enum.GetValues<CheckStatus>())
        {
            RussianText.AssertRussian(new EnvironmentCheck("x", status, "y").StatusText, $"{status}");
        }
    }

    [Fact]
    public void A_prerequisite_says_in_russian_what_skipping_it_costs()
    {
        InstallerCulture.Current = InstallerLanguage.Russian;

        var optional = new DependencySelection("dotnet", ".NET SDK 10", IsRequired: false)
        {
            Consequence = "без него определения ищутся текстом",
        };

        RussianText.AssertRussian(optional.StateText, "state");
        RussianText.AssertRussian(optional.ActionText, "action");
        Assert.Equal("необязателен: без него определения ищутся текстом", optional.RequirementText);
        Assert.Equal(
            "обязателен",
            new DependencySelection("git", "Git", IsRequired: true).RequirementText);
    }

    /// <summary>
    /// The row used to join two clauses with the same em dash it was already joined by, which
    /// read as ".NET SDK 10 — Not installed · optional — without it, …". The inner separator is
    /// a colon in both languages: one dash per line, and the reader can tell which half is which.
    /// </summary>
    [Fact]
    public void The_english_requirement_line_uses_one_separator_per_clause()
    {
        InstallerCulture.Current = InstallerLanguage.English;

        var optional = new DependencySelection("node", "Node.js", IsRequired: false)
        {
            Consequence = "only needed to run the TypeScript indexer",
        };

        Assert.Equal("optional: only needed to run the TypeScript indexer", optional.RequirementText);
    }

    [Fact]
    public void Every_client_integration_names_itself_in_russian()
    {
        InstallerCulture.Current = InstallerLanguage.Russian;

        foreach (var choice in Enum.GetValues<AgentChoice>())
        {
            RussianText.AssertRussian(choice.Title(), $"Title({choice})");
            RussianText.AssertRussian(choice.Description(), $"Description({choice})");
        }
    }

    /// <summary>
    /// The choice combo box had no display projection over its list of enum values, so the page
    /// where the choice is actually made rendered "McpAndInstructions" and "NoChange" — the same
    /// defect the review page had already fixed for the residency policy. Translating
    /// <see cref="AgentChoiceMapping.Title"/> changes nothing on screen until the box is given
    /// something to display, so the option carries its own title.
    /// </summary>
    [Fact]
    public void The_choice_list_offers_titles_rather_than_enum_names()
    {
        InstallerCulture.Current = InstallerLanguage.Russian;

        var page = new AgentIntegrationPageViewModel();

        Assert.NotEmpty(page.ChoiceOptions);
        foreach (var option in page.ChoiceOptions)
        {
            RussianText.AssertRussian(option.Title, $"{option.Choice} title");
        }
    }

    [Fact]
    public void An_agent_row_is_one_heading_rather_than_four_runs()
    {
        InstallerCulture.Current = InstallerLanguage.Russian;

        var option = new AgentOption("claude", AgentChoice.McpAndInstructions) { IsDetected = true };

        Assert.Equal("Claude Code (обнаружено)", option.Heading);
    }

    [Fact]
    public void The_english_pages_are_left_exactly_as_they_were()
    {
        InstallerCulture.Current = InstallerLanguage.English;

        Assert.Equal("Not found", new EnvironmentCheck("x", CheckStatus.Missing, "y").StatusText);
        Assert.Equal("Leave unchanged", AgentChoice.NoChange.Title());
        Assert.Equal(
            "Codex (not detected)",
            new AgentOption("codex", AgentChoice.NoChange).Heading);
    }
}
