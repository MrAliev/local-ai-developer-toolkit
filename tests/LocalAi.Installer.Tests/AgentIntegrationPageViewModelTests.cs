using LocalAi.Installer.Core.Diagnosis;
using LocalAi.Installer.ViewModels;

namespace LocalAi.Installer.Tests;

public sealed class AgentIntegrationPageViewModelTests
{
    private static AgentSnapshot Detected(AgentKind kind) =>
        new(
            kind,
            new FileMetadataSnapshot($@"C:\{kind}\client.exe", true, 1, null, null),
            FileMetadataSnapshot.Absent(string.Empty),
            FileMetadataSnapshot.Absent(string.Empty));

    private static AgentChoice ChoiceOf(AgentIntegrationPageViewModel page, string agent) =>
        page.Agents.Single(option => option.Agent == agent).Choice;

    [Fact]
    public void A_detected_client_is_configured_by_default()
    {
        var page = new AgentIntegrationPageViewModel();

        page.ApplyDetection([Detected(AgentKind.Claude)]);

        // Registering the servers and writing the instructions is the point of installing at
        // all, so a client that exists is set up unless the user objects.
        Assert.Equal(AgentChoice.McpAndInstructions, ChoiceOf(page, "claude"));
        // Nothing was found for Codex, so nothing is proposed for it.
        Assert.Equal(AgentChoice.NoChange, ChoiceOf(page, "codex"));
    }

    [Fact]
    public void An_explicit_choice_survives_a_later_detection_pass()
    {
        var page = new AgentIntegrationPageViewModel();
        page.ApplyDetection([Detected(AgentKind.Claude), Detected(AgentKind.Codex)]);

        page.SetChoice("claude", AgentChoice.NoChange);
        page.SetChoice("codex", AgentChoice.McpOnly);
        // Detection runs again whenever a prerequisite is installed.
        page.ApplyDetection([Detected(AgentKind.Claude), Detected(AgentKind.Codex)]);

        Assert.Equal(AgentChoice.NoChange, ChoiceOf(page, "claude"));
        Assert.Equal(AgentChoice.McpOnly, ChoiceOf(page, "codex"));
    }

    [Fact]
    public void A_client_that_disappears_loses_the_default_it_was_given()
    {
        var page = new AgentIntegrationPageViewModel();
        page.ApplyDetection([Detected(AgentKind.Claude)]);

        page.ApplyDetection([]);

        Assert.Equal(AgentChoice.NoChange, ChoiceOf(page, "claude"));
    }
}
