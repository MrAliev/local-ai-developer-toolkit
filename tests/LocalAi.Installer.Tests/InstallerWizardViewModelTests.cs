using LocalAi.Contracts;
using LocalAi.Installer.Core.Planning;
using LocalAi.Installer.ViewModels;

namespace LocalAi.Installer.Tests;

public sealed class InstallerWizardViewModelTests
{
    private static InstallerWizardViewModel SupportedWizard()
    {
        var wizard = new InstallerWizardViewModel();
        wizard.Diagnose.SetResult(true);
        return wizard;
    }

    [Fact]
    public void Unsupported_environment_blocks_the_first_page()
    {
        var wizard = new InstallerWizardViewModel();
        wizard.Diagnose.SetResult(false, "unsupported cpu");

        Assert.False(wizard.CanMoveNext);
        Assert.False(wizard.MoveNext());

        wizard.Diagnose.SetResult(true);
        Assert.True(wizard.MoveNext());
        Assert.Equal(InstallerPage.Dependencies, wizard.CurrentPage);
    }

    [Fact]
    public void Nothing_is_consented_before_the_user_says_so()
    {
        var wizard = SupportedWizard();

        Assert.All(
            wizard.Dependencies.Dependencies,
            dependency => Assert.False(dependency.IsConsented));
        Assert.False(wizard.Review.IsConfirmed);
    }

    [Fact]
    public void A_required_dependency_blocks_until_it_is_installed_or_selected()
    {
        var wizard = SupportedWizard();
        wizard.MoveNext();

        Assert.False(wizard.Dependencies.CanContinue);

        wizard.Dependencies.SetConsent("Git", true);
        Assert.False(wizard.Dependencies.CanContinue);

        wizard.Dependencies.SetInstalled("Ollama", true);
        wizard.Dependencies.SetInstalled("GitHubCli", true);
        Assert.True(wizard.Dependencies.CanContinue);
    }

    [Fact]
    public void Detecting_a_dependency_does_not_grant_consent_to_reinstall_it()
    {
        var wizard = SupportedWizard();

        wizard.Dependencies.SetInstalled("Git", true);

        var git = wizard.Dependencies.Dependencies.Single(item => item.Id == "Git");
        Assert.True(git.IsInstalled);
        Assert.False(git.IsConsented);
    }

    [Fact]
    public void A_dependency_without_an_installer_cannot_be_selected()
    {
        var wizard = SupportedWizard();

        wizard.Dependencies.SetConsent("VisualCpp", true);

        var msvc = wizard.Dependencies.Dependencies.Single(item => item.Id == "VisualCpp");
        Assert.False(msvc.IsInstallable);
        Assert.False(msvc.IsConsented);
    }

    [Fact]
    public void The_package_page_never_claims_a_release_it_has_not_resolved()
    {
        var wizard = SupportedWizard();

        Assert.False(wizard.Package.HasPackage);
        Assert.False(wizard.Package.IsCompatible);

        wizard.Package.ReportUnavailable("no manifest published");

        Assert.False(wizard.Package.HasPackage);
        Assert.Contains("not resolved", wizard.Package.ReviewText, StringComparison.Ordinal);
        // An unresolved package must not trap the user on this page.
        Assert.True(wizard.Package.CanContinue);
    }

    [Fact]
    public void Exact_model_selection_offers_only_catalogue_models()
    {
        var wizard = SupportedWizard();

        // Everything offered must be routable: a model outside the catalogue cannot be
        // loaded at all, so it must never appear as a choice.
        Assert.NotEmpty(wizard.Models.CatalogModels);
        Assert.All(
            wizard.Models.CatalogModels,
            model =>
            {
                Assert.NotEmpty(model.Capabilities);
                Assert.NotEmpty(model.ContextTokens);
            });
    }

    [Fact]
    public void Exact_model_selection_restricts_contexts_to_the_selected_model()
    {
        var wizard = SupportedWizard();
        wizard.Models.Mode = ModelSelectionMode.ChooseExact;

        var model = wizard.Models.CatalogModels.First();
        wizard.Models.SelectedModel = model;

        Assert.Equal(
            [.. model.ContextTokens.OrderBy(value => value)],
            wizard.Models.AvailableContexts);
        Assert.Contains(wizard.Models.SelectedContext, model.ContextTokens);
        Assert.True(wizard.Models.CanContinue);
        Assert.Contains(model.Tag, wizard.Models.ReviewText, StringComparison.Ordinal);
    }

    [Fact]
    public void Switching_model_keeps_a_context_both_models_permit()
    {
        var wizard = SupportedWizard();
        wizard.Models.Mode = ModelSelectionMode.ChooseExact;

        var first = wizard.Models.CatalogModels.First();
        wizard.Models.SelectedModel = first;
        var shared = wizard.Models.AvailableContexts.First();
        wizard.Models.SelectedContext = shared;

        foreach (var candidate in wizard.Models.CatalogModels.Skip(1))
        {
            wizard.Models.SelectedModel = candidate;
            // Either the deliberate choice survived, or it was replaced by one this model
            // actually permits — never left at an unsupported value.
            Assert.Contains(wizard.Models.SelectedContext, candidate.ContextTokens);
        }
    }

    [Fact]
    public void Residency_defaults_to_strict_and_warns_only_when_relaxed()
    {
        var wizard = SupportedWizard();

        Assert.Equal(ModelResidencyPolicy.RequireFullVram, wizard.Residency.Policy);
        Assert.False(wizard.Residency.HasWarning);

        wizard.Residency.IsAllowCpu = true;

        Assert.Equal(ModelResidencyPolicy.AllowCpu, wizard.Residency.Policy);
        Assert.True(wizard.Residency.HasWarning);
    }

    [Fact]
    public void A_missing_adapter_hints_but_does_not_change_the_choice()
    {
        var wizard = SupportedWizard();

        wizard.Residency.HasUsableAdapter = false;

        Assert.True(wizard.Residency.HasAdapterHint);
        // The installer states the consequence; it does not relax the policy on its own.
        Assert.Equal(ModelResidencyPolicy.RequireFullVram, wizard.Residency.Policy);
    }

    [Fact]
    public void Agents_default_to_leaving_a_client_alone_and_map_onto_the_core_choices()
    {
        var wizard = SupportedWizard();

        Assert.All(
            wizard.Agents.Agents,
            agent => Assert.Equal(AgentChoice.NoChange, agent.Choice));

        wizard.Agents.SetChoice("claude", AgentChoice.McpAndInstructions);

        Assert.Equal(
            AgentIntegrationChoice.McpAndInstructions,
            wizard.Agents.Agents.Single(agent => agent.Agent == "claude").Choice.ToCore());
        Assert.Equal(
            AgentIntegrationChoice.NoChange,
            AgentChoice.NoChange.ToCore());
    }

    [Fact]
    public void Navigation_reaches_confirm_and_install_needs_an_explicit_confirmation()
    {
        var wizard = SupportedWizard();
        wizard.Dependencies.SetInstalled("Git", true);
        wizard.Dependencies.SetInstalled("Ollama", true);
        wizard.Dependencies.SetInstalled("GitHubCli", true);

        Assert.True(wizard.MoveNext()); // Dependencies
        Assert.True(wizard.MoveNext()); // Package
        Assert.True(wizard.MoveNext()); // Models
        Assert.True(wizard.MoveNext()); // Residency
        Assert.True(wizard.MoveNext()); // Agents
        Assert.True(wizard.MoveNext()); // Confirm
        Assert.Equal(InstallerPage.Confirm, wizard.CurrentPage);

        Assert.False(wizard.CanRun);
        wizard.SetReviewConfirmed(true);
        Assert.True(wizard.CanRun);
    }

    [Fact]
    public void Confirm_offers_install_instead_of_next_and_keeps_back_available()
    {
        var wizard = SupportedWizard();
        wizard.Dependencies.SetInstalled("Git", true);
        wizard.Dependencies.SetInstalled("Ollama", true);
        wizard.Dependencies.SetInstalled("GitHubCli", true);
        for (var step = 0; step < 6; step++)
        {
            wizard.MoveNext();
        }

        Assert.Equal(InstallerPage.Confirm, wizard.CurrentPage);
        Assert.False(wizard.IsNextVisible);
        Assert.True(wizard.IsInstallVisible);
        Assert.True(wizard.CanMovePrevious);
        Assert.True(wizard.CanCancel);
    }

    [Fact]
    public void Back_is_unavailable_on_the_first_page()
    {
        var wizard = SupportedWizard();

        Assert.False(wizard.CanMovePrevious);
        Assert.False(wizard.MovePrevious());
    }

    [Fact]
    public void The_review_summarises_every_page_and_repeats_a_relaxed_residency_warning()
    {
        var wizard = SupportedWizard();
        wizard.Dependencies.SetConsent("Git", true);
        wizard.Models.Mode = ModelSelectionMode.Skip;
        wizard.Residency.IsAllowCpu = true;
        wizard.Agents.SetChoice("claude", AgentChoice.McpOnly);

        var review = wizard.ReviewText!;

        Assert.Contains("LocalAi package", review, StringComparison.Ordinal);
        Assert.Contains("Git", review, StringComparison.Ordinal);
        Assert.Contains("skipped", review, StringComparison.Ordinal);
        Assert.Contains("AllowCpu", review, StringComparison.Ordinal);
        Assert.Contains("Claude Code", review, StringComparison.Ordinal);
        Assert.Contains("Warning", review, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_dry_run_reports_completion_without_installing_anything()
    {
        var wizard = SupportedWizard();
        wizard.Dependencies.SetInstalled("Git", true);
        wizard.Dependencies.SetInstalled("Ollama", true);
        wizard.Dependencies.SetInstalled("GitHubCli", true);
        for (var step = 0; step < 6; step++)
        {
            wizard.MoveNext();
        }

        wizard.SetReviewConfirmed(true);
        Assert.True(await wizard.RunAsync(TestContext.Current.CancellationToken));

        Assert.True(wizard.IsComplete);
        Assert.False(wizard.HasRunError);
        Assert.Equal(InstallerPage.Finish, wizard.CurrentPage);
        Assert.Equal("Installation complete", wizard.StepTitle);
    }

    [Fact]
    public void Cancelling_outside_a_run_asks_the_window_to_close()
    {
        var wizard = SupportedWizard();
        var closed = false;
        wizard.CloseRequested += (_, _) => closed = true;

        wizard.Cancel();

        Assert.True(closed);
    }
}
