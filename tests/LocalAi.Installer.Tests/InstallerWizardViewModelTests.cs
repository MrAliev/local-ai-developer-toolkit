using LocalAi.Installer.ViewModels;

namespace LocalAi.Installer.Tests;

public sealed class InstallerWizardViewModelTests
{
    [Fact]
    public void Navigator_blocks_unsupported_diagnosis_and_advances_when_supported()
    {
        var vm = new InstallerWizardViewModel();
        vm.Diagnose.SetResult(false, "unsupported cpu");

        Assert.False(vm.CanMoveNext);
        Assert.False(vm.MoveNext());

        vm.Diagnose.SetResult(true);
        Assert.True(vm.MoveNext());
        Assert.Equal(InstallerPage.Dependencies, vm.CurrentPage);
    }

    [Fact]
    public void Dependencies_supports_independent_consent()
    {
        var vm = new InstallerWizardViewModel();
        vm.MoveNext();
        var dependencies = vm.Dependencies;

        dependencies.SetConsent("Git", true);
        dependencies.MarkInstalled("Ollama");
        Assert.NotEqual(dependencies.Dependencies[0].IsConsented, dependencies.Dependencies[1].IsConsented);

        dependencies.SetConsent("VisualCpp", true);
        Assert.True(vm.Dependencies.CanContinue);
    }

    [Fact]
    public void Model_page_requires_manual_values_when_mode_is_manual()
    {
        var vm = new InstallerWizardViewModel();
        vm.MoveNext(); // Dependencies
        vm.MoveNext(); // Package

        vm.Models.Mode = ModelSelectionMode.Manual;
        vm.Models.ManualContextWindow = 8192;
        vm.Models.ManualModelId = "llama3.1";

        Assert.True(vm.Models.CanContinue);
        Assert.Contains("manual llama3.1", vm.Models.ReviewText);
    }

    [Fact]
    public void Agents_allow_fourway_choices_per_agent()
    {
        var vm = new InstallerWizardViewModel();
        vm.MoveNext(); // Dependencies
        vm.MoveNext(); // Package
        vm.MoveNext(); // Models

        vm.Agents.SetChoice("codex", AgentChoice.InstallManagedBlock);
        vm.Agents.SetChoice("claude", AgentChoice.ConfigureExisting);

        Assert.Equal(2, vm.Agents.Agents.Count);
        Assert.True(vm.Agents.CanContinue);
        Assert.Contains("codex:InstallManagedBlock", vm.Agents.ReviewText, StringComparison.Ordinal);
    }

    [Fact]
    public void Review_renders_exact_summary_and_supports_confirmation_run_restart_notice()
    {
        var vm = new InstallerWizardViewModel();
        vm.Diagnose.SetResult(true);
        vm.MoveNext(); // Dependencies
        vm.Dependencies.SetConsent("Git", true);
        vm.MoveNext(); // Package
        vm.Package.SelectCompatibleRelease("0.1.2", true);
        vm.MoveNext(); // Models
        vm.Models.Mode = ModelSelectionMode.Skip;
        vm.MoveNext(); // Agents
        vm.Agents.SetChoice("codex", AgentChoice.RunWithoutAgent);
        vm.Agents.SetChoice("claude", AgentChoice.Skip);
        vm.Review.IsConfirmed = true;
        vm.MoveNext(); // Review

        var review = vm.ReviewText!;
        Assert.Contains("OS supported: True", review);
        Assert.Contains("Agents:", review);
        Assert.True(vm.CanRun);

        vm.SetProgress(35, "Installing");
        vm.SetRollbackInfo("Manual restore path available.", true);
        Assert.Equal(35, vm.Progress);
        Assert.Equal("Installing", vm.ProgressText);
        Assert.Equal("Manual restore path available.", vm.RollbackResult);
        Assert.True(vm.RequiresRestart);

        vm.ConfirmReview();
        Assert.True(vm.CanRun);
        Assert.True(vm.Run());
        Assert.True(vm.IsComplete);
        Assert.Equal("Completed", vm.ProgressText);
        Assert.Equal(InstallerPage.Finish, vm.CurrentPage);
    }

    [Fact]
    public void Cancellation_moves_state_to_canceled_and_blocks_navigation()
    {
        var vm = new InstallerWizardViewModel();
        vm.Diagnose.SetResult(true);
        vm.Cancel();

        Assert.True(vm.IsCanceled);
        Assert.False(vm.CanMoveNext);
        Assert.False(vm.MoveNext());
    }

    [Fact]
    public void Language_switching_switches_display_language()
    {
        var vm = new InstallerWizardViewModel();

        vm.Language = "ru-RU";

        Assert.True(vm.IsRussian);
        Assert.Equal("ru-RU", InstallerCulture.CurrentCultureCode);
    }
}
