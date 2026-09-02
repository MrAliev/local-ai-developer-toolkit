using LocalAi.Contracts;
using LocalAi.Installer.Core.Dependencies;
using LocalAi.Installer.Core.Planning;
using LocalAi.Installer.Core.Transactions;
using LocalAi.Installer.ViewModels;

namespace LocalAi.Installer.Tests;

public sealed class InstallerWizardViewModelTests
{
    private static InstallerWizardViewModel SupportedWizard()
    {
        var wizard = new InstallerWizardViewModel();
        // The real wizard clears this when the environment probe finishes.
        wizard.Diagnose.IsChecking = false;
        wizard.Diagnose.SetResult(true);
        return wizard;
    }

    [Fact]
    public void The_first_page_holds_the_user_while_the_check_is_running()
    {
        var wizard = new InstallerWizardViewModel();

        // Detection has not finished, so there is nothing to move on from yet.
        Assert.True(wizard.Diagnose.IsChecking);
        Assert.False(wizard.Diagnose.HasResults);
        Assert.False(wizard.CanMoveNext);

        wizard.Diagnose.IsChecking = false;
        wizard.Diagnose.SetResult(true);

        Assert.True(wizard.Diagnose.HasResults);
        Assert.True(wizard.CanMoveNext);
    }

    [Fact]
    public void Unsupported_environment_blocks_the_first_page()
    {
        var wizard = new InstallerWizardViewModel();
        wizard.Diagnose.IsChecking = false;
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

        // Git and Ollama, and nothing else: Git is how a repository is identified, scanned
        // and hooked, Ollama is what computes the embeddings. The rest buys precise
        // navigation for one language and must not hold an installation hostage.
        wizard.Dependencies.SetInstalled("Ollama", true);
        Assert.True(wizard.Dependencies.CanContinue);
    }

    /// <summary>
    /// Three to four gigabytes and several UAC prompts, because winget installs these
    /// machine-wide -- demanded from somebody who may only want semantic search over C#.
    /// </summary>
    [Theory]
    [InlineData("GitHubCli")]
    [InlineData("DotNetSdk")]
    [InlineData("NodeJs")]
    [InlineData("ScipTypeScript")]
    [InlineData("Python")]
    [InlineData("ScipPython")]
    public void An_optional_dependency_does_not_hold_the_wizard(string id)
    {
        var wizard = SupportedWizard();
        wizard.MoveNext();
        wizard.Dependencies.SetInstalled("Git", true);
        wizard.Dependencies.SetInstalled("Ollama", true);

        var dependency = wizard.Dependencies.Dependencies.Single(item => item.Id == id);

        Assert.False(dependency.IsRequired);
        Assert.True(wizard.Dependencies.CanContinue);
    }

    /// <summary>
    /// "Optional" on its own invites skipping everything and finding the cost later, at the
    /// point where a tool quietly stops answering precisely.
    /// </summary>
    [Fact]
    public void Every_optional_dependency_says_what_skipping_it_gives_up()
    {
        var wizard = SupportedWizard();

        var optional = wizard.Dependencies.Dependencies
            .Where(dependency => !dependency.IsRequired)
            .ToArray();

        Assert.NotEmpty(optional);
        Assert.All(optional, dependency =>
        {
            Assert.NotEmpty(dependency.Consequence);
            // A colon, not the em dash the whole line is already joined by.
            Assert.Contains(
                "optional: ",
                dependency.RequirementText,
                StringComparison.Ordinal);
        });
    }

    [Fact]
    public void Only_git_and_ollama_are_required()
    {
        var wizard = SupportedWizard();

        Assert.Equal(
            ["Git", "Ollama"],
            wizard.Dependencies.Dependencies
                .Where(dependency => dependency.IsRequired)
                .Select(dependency => dependency.Id));
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
    public void Every_listed_prerequisite_is_one_the_installer_can_act_on()
    {
        var wizard = SupportedWizard();

        // The MSVC redistributable used to sit here purely as decoration: nothing detected
        // it, nothing installed it and nothing needed it. The page must not regain items
        // that inform nobody.
        Assert.NotEmpty(wizard.Dependencies.Dependencies);
        Assert.All(
            wizard.Dependencies.Dependencies,
            dependency =>
            {
                Assert.True(dependency.IsInstallable);
                Assert.NotNull(
                    DependencyCatalog.Supported.SingleOrDefault(
                        definition => definition.DisplayName == dependency.Title));
            });
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
        wizard.Dependencies.SetInstalled("DotNetSdk", true);
        wizard.Dependencies.SetInstalled("NodeJs", true);
        wizard.Dependencies.SetInstalled("ScipTypeScript", true);
        wizard.Dependencies.SetInstalled("Python", true);
        wizard.Dependencies.SetInstalled("ScipPython", true);

        Assert.True(wizard.MoveNext()); // Dependencies
        Assert.True(wizard.MoveNext()); // Package
        Assert.True(wizard.MoveNext()); // Models and memory
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
        wizard.Dependencies.SetInstalled("DotNetSdk", true);
        wizard.Dependencies.SetInstalled("NodeJs", true);
        wizard.Dependencies.SetInstalled("ScipTypeScript", true);
        wizard.Dependencies.SetInstalled("Python", true);
        wizard.Dependencies.SetInstalled("ScipPython", true);
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
        // The rule in the words the page offered it, not the enum: this list is read by
        // a person about to consent to it.
        Assert.Contains("running on the processor", review, StringComparison.Ordinal);
        Assert.DoesNotContain("AllowCpu", review, StringComparison.Ordinal);
        Assert.Contains("Claude Code", review, StringComparison.Ordinal);
        Assert.Contains("Warning", review, StringComparison.Ordinal);
    }

    /// <summary>
    /// The confirmation page is the last screen before anything is applied, so the one
    /// omission that costs the whole point of the run has to read as a warning there. An
    /// unresolved package leaves the clients unconfigured too, and the package line alone —
    /// "not resolved" among four neutral statements — is what a person skims past.
    /// </summary>
    [Fact]
    public void The_review_warns_when_no_release_was_verified()
    {
        var wizard = SupportedWizard();
        wizard.Dependencies.SetConsent("Git", true);

        var review = wizard.ReviewText!;

        Assert.Contains("Warning", review, StringComparison.Ordinal);
        Assert.Contains("will not be installed", review, StringComparison.Ordinal);
        Assert.Contains("left unconfigured", review, StringComparison.Ordinal);
    }

    /// <summary>
    /// Nothing in the wizard asks anyone to sign in to GitHub any more. Releases are public
    /// and read over plain HTTPS, so an instruction to run `gh auth login` would send someone
    /// to create an account for a download they are already allowed to make.
    /// </summary>
    [Fact]
    public void No_page_asks_for_a_GitHub_account()
    {
        var wizard = SupportedWizard();

        Assert.DoesNotContain("gh auth login", wizard.ReviewText!, StringComparison.Ordinal);
        Assert.DoesNotContain(
            "gh auth login",
            wizard.Package.StatusText,
            StringComparison.Ordinal);
        Assert.All(
            wizard.Dependencies.Dependencies.Where(dependency => dependency.IsRequired),
            dependency => Assert.DoesNotContain(
                "GitHub",
                dependency.Title,
                StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_dry_run_reports_completion_without_installing_anything()
    {
        var wizard = SupportedWizard();
        wizard.Dependencies.SetInstalled("Git", true);
        wizard.Dependencies.SetInstalled("Ollama", true);
        wizard.Dependencies.SetInstalled("GitHubCli", true);
        wizard.Dependencies.SetInstalled("DotNetSdk", true);
        wizard.Dependencies.SetInstalled("NodeJs", true);
        wizard.Dependencies.SetInstalled("ScipTypeScript", true);
        wizard.Dependencies.SetInstalled("Python", true);
        wizard.Dependencies.SetInstalled("ScipPython", true);
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

    /// <summary>
    /// A dry run applies nothing, so it must record nothing: a journal describing effects
    /// that never happened is the same lie the removed transactional installer told, with
    /// the direction reversed.
    /// </summary>
    [Fact]
    public async Task A_dry_run_writes_no_journal_and_offers_no_rollback()
    {
        var logDirectory = TestLogDirectory();
        try
        {
            var wizard = WizardAtConfirm();
            wizard.LogDirectory = logDirectory;

            wizard.SetReviewConfirmed(true);
            Assert.True(await wizard.RunAsync(TestContext.Current.CancellationToken));

            Assert.False(wizard.CanRollback);
            Assert.False(
                Directory.Exists(logDirectory) &&
                Directory.EnumerateFiles(logDirectory, "journal-*.json").Any());
        }
        finally
        {
            DeleteDirectory(logDirectory);
        }
    }

    /// <summary>
    /// The interesting failure is the one where the wizard never ran its own cleanup: the
    /// process was killed mid-install. The next start has to say what that run applied
    /// instead of pretending the machine is clean.
    /// </summary>
    [Fact]
    public void An_interrupted_run_is_reported_on_the_first_page()
    {
        var logDirectory = TestLogDirectory();
        try
        {
            var journal = InstallerRunJournal.Start(logDirectory);
            var applied = journal.BeginStep(
                InstallerRunEffectKind.ResidencyPolicy,
                "Model residency policy (RequireFullVram)");
            journal.CompleteStep(applied, "written", isReversible: true);
            journal.BeginStep(
                InstallerRunEffectKind.AgentConfiguration,
                "Claude client configuration");

            // Releases the live lock the way process death does; a held lock means the run
            // is still alive in another window and must not be offered back.
            journal.Dispose();

            var wizard = SupportedWizard();
            wizard.LogDirectory = logDirectory;
            wizard.LoadInterruptedRunJournal();

            Assert.True(wizard.HasInterruptedRun);
            Assert.NotNull(wizard.InterruptedRunNotice);
            Assert.Contains("Model residency policy", wizard.InterruptedRunNotice);
            Assert.Contains("applied", wizard.InterruptedRunNotice);
            Assert.Contains("state unknown", wizard.InterruptedRunNotice);
            Assert.Contains("roll back", wizard.InterruptedRunNotice);
        }
        finally
        {
            DeleteDirectory(logDirectory);
        }
    }

    /// <summary>
    /// Continuing past the notice is the explicit "leave it in place" choice it offers.
    /// Without recording that, every later start would ask about the same abandoned run.
    /// </summary>
    [Fact]
    public void Moving_past_the_first_page_abandons_the_interrupted_journal()
    {
        var logDirectory = TestLogDirectory();
        try
        {
            var journal = InstallerRunJournal.Start(logDirectory);
            var step = journal.BeginStep(
                InstallerRunEffectKind.ResidencyPolicy,
                "Model residency policy (RequireFullVram)");
            journal.CompleteStep(step, "written", isReversible: true);

            // Releases the live lock the way process death does; a held lock means the run
            // is still alive in another window and must not be offered back.
            journal.Dispose();

            var wizard = SupportedWizard();
            wizard.LogDirectory = logDirectory;
            wizard.LoadInterruptedRunJournal();
            Assert.True(wizard.HasInterruptedRun);

            Assert.True(wizard.MoveNext());

            Assert.False(wizard.HasInterruptedRun);
            Assert.Null(InstallerRunJournal.FindInterrupted(logDirectory));
            Assert.Equal(
                InstallerRunOutcome.Abandoned,
                InstallerRunJournal.Load(journal.JournalPath).Snapshot.Outcome);
        }
        finally
        {
            DeleteDirectory(logDirectory);
        }
    }

    [Fact]
    public async Task Rolling_back_a_previous_run_undoes_its_files_and_reports_each_effect()
    {
        var logDirectory = TestLogDirectory();
        try
        {
            var createdFile = Path.Combine(logDirectory, "runtime", "ollama-launch.json");
            Directory.CreateDirectory(Path.GetDirectoryName(createdFile)!);
            var written = "{\"path\":\"C:/ollama.exe\"}"u8.ToArray();
            File.WriteAllBytes(createdFile, written);
            var journal = InstallerRunJournal.Start(logDirectory);
            var fileStep = journal.BeginStep(
                InstallerRunEffectKind.OllamaLaunchRecord,
                "Ollama start-on-demand record");
            journal.CompleteStep(
                fileStep,
                "written",
                isReversible: true,
                new InstallerRunUndoData(Files:
                [
                    new InstallerRunFileUndo(
                        createdFile,
                        false,
                        InstallerRunJournal.Sha256Hex([]),
                        null,
                        null,
                        InstallerRunJournal.Sha256Hex(written)),
                ]));
            var dependencyStep = journal.BeginStep(
                InstallerRunEffectKind.DependencyInstall,
                "Prerequisite Git (Git.Git)");
            journal.CompleteStep(dependencyStep, "Installed machine-wide.", isReversible: false);

            // Releases the live lock the way process death does; a held lock means the run
            // is still alive in another window and must not be offered back.
            journal.Dispose();

            var wizard = SupportedWizard();
            wizard.LogDirectory = logDirectory;
            wizard.LoadInterruptedRunJournal();
            Assert.True(wizard.HasInterruptedRun);

            await wizard.RollbackPreviousRunAsync();

            Assert.False(File.Exists(createdFile));
            Assert.False(wizard.HasInterruptedRun);
            Assert.NotNull(wizard.InterruptedRunNotice);
            Assert.Contains("Ollama start-on-demand record: undone", wizard.InterruptedRunNotice);
            Assert.Contains("Prerequisite Git (Git.Git): left in place", wizard.InterruptedRunNotice);
            Assert.Null(InstallerRunJournal.FindInterrupted(logDirectory));
        }
        finally
        {
            DeleteDirectory(logDirectory);
        }
    }

    private static string TestLogDirectory() => Path.Combine(
        Path.GetTempPath(),
        "LocalAi.Installer.WizardJournal.Tests",
        Guid.NewGuid().ToString("N"));

    private static void DeleteDirectory(string directory)
    {
        if (Directory.Exists(directory))
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static InstallerWizardViewModel WizardAtConfirm()
    {
        var wizard = SupportedWizard();
        wizard.Dependencies.SetInstalled("Git", true);
        wizard.Dependencies.SetInstalled("Ollama", true);
        wizard.Dependencies.SetInstalled("GitHubCli", true);
        wizard.Dependencies.SetInstalled("DotNetSdk", true);
        wizard.Dependencies.SetInstalled("NodeJs", true);
        wizard.Dependencies.SetInstalled("ScipTypeScript", true);
        wizard.Dependencies.SetInstalled("Python", true);
        wizard.Dependencies.SetInstalled("ScipPython", true);
        for (var step = 0; step < 6; step++)
        {
            wizard.MoveNext();
        }

        return wizard;
    }
}
