using System.Runtime.InteropServices;
using LocalAi.Installer.Core.Diagnosis;
using LocalAi.Installer.Core.Planning;

namespace LocalAi.Installer.Core.Tests;

public sealed class InstallerPlanTests
{
    private static readonly Guid ExpectedPlanId =
        Guid.Parse("02d97f9a-ae85-4e50-9888-2a5875366599");
    private static readonly DateTimeOffset ExpectedCreatedAt =
        new(2026, 7, 31, 9, 30, 0, TimeSpan.Zero);

    [Theory]
    [InlineData(MutationKind.Dependency)]
    [InlineData(MutationKind.Package)]
    [InlineData(MutationKind.Model)]
    [InlineData(MutationKind.Agent)]
    public void Selected_mutation_without_consent_is_rejected(MutationKind kind)
    {
        var input = ValidInput();
        input = kind switch
        {
            MutationKind.Dependency => input with
            {
                Dependencies =
                [
                    new("dependency.git", "Git.Git", "2.50.1", true, false),
                ],
            },
            MutationKind.Package => input with
            {
                Package = input.Package with { ConsentGranted = false },
            },
            MutationKind.Model => input with
            {
                Models =
                [
                    new("model.qwen", "qwen3:8b", 32_768, true, false),
                ],
            },
            MutationKind.Agent => input with
            {
                Agents =
                [
                    new(
                        "agent.codex",
                        AgentKind.Codex,
                        AgentIntegrationChoice.McpOnly,
                        @"C:\Users\test\.codex\config.toml",
                        null,
                        true,
                        false),
                ],
            },
            _ => throw new ArgumentOutOfRangeException(nameof(kind)),
        };

        Assert.Throws<InvalidOperationException>(() => Build(input));
    }

    [Fact]
    public void Unselected_and_no_change_actions_are_accepted_without_consent()
    {
        var input = ValidInput() with
        {
            Dependencies =
            [
                new("dependency.git", "Git.Git", "2.50.1", false, false),
            ],
            Package = new(
                "package.localai",
                "1.2.3",
                @"C:\Downloads\localai.zip",
                false,
                false),
            Models =
            [
                new("model.qwen", "qwen3:8b", 32_768, false, false),
            ],
            Agents =
            [
                new(
                    "agent.codex",
                    AgentKind.Codex,
                    AgentIntegrationChoice.NoChange,
                    null,
                    null,
                    false,
                    false),
            ],
        };

        var plan = Build(input);

        Assert.False(plan.Dependencies[0].Selected);
        Assert.False(plan.Package.Selected);
        Assert.False(plan.Models[0].Selected);
        Assert.Equal(AgentIntegrationChoice.NoChange, plan.Agents[0].Choice);
    }

    [Fact]
    public void Build_defensively_snapshots_actions_and_nested_diagnosis_collections()
    {
        var adapters = new List<GpuAdapterSnapshot>
        {
            new("gpu-1", "GPU 1", 8_000, false),
        };
        var diagnosisAgents = new List<AgentSnapshot>
        {
            AgentSnapshot.Absent(AgentKind.Codex),
        };
        var diagnosis = SupportedDiagnosis(adapters, diagnosisAgents);
        var dependencies = new List<DependencyAction>
        {
            new("dependency.git", "Git.Git", "2.50.1", false, false),
        };
        var models = new List<ModelInstallAction>();
        var agents = new List<AgentConfigurationAction>();
        var effects = new List<NonTransactionalEffect>();
        var input = ValidInput() with
        {
            Diagnosis = diagnosis,
            Dependencies = dependencies,
            Models = models,
            Agents = agents,
            Effects = effects,
        };

        var plan = Build(input);
        dependencies.Add(new("dependency.ollama", "Ollama.Ollama", "0.11.5", false, false));
        models.Add(new("model.qwen", "qwen3:8b", 32_768, false, false));
        agents.Add(new(
            "agent.codex",
            AgentKind.Codex,
            AgentIntegrationChoice.NoChange,
            null,
            null,
            false,
            false));
        effects.Add(new("effect.late", "dependency.git", "late effect"));
        adapters.Add(new("gpu-2", "GPU 2", 16_000, false));
        diagnosisAgents.Add(AgentSnapshot.Absent(AgentKind.Claude));

        Assert.NotSame(diagnosis, plan.Diagnosis);
        Assert.NotSame(diagnosis.Gpu, plan.Diagnosis.Gpu);
        Assert.NotSame(diagnosis.Gpu.Adapters, plan.Diagnosis.Gpu.Adapters);
        Assert.NotSame(diagnosis.Agents, plan.Diagnosis.Agents);
        Assert.Single(plan.Dependencies);
        Assert.Empty(plan.Models);
        Assert.Empty(plan.Agents);
        Assert.Empty(plan.NonTransactionalEffects);
        Assert.Single(plan.Diagnosis.Gpu.Adapters);
        Assert.Single(plan.Diagnosis.Agents);
        Assert.Throws<NotSupportedException>(
            () => ((IList<DependencyAction>)plan.Dependencies).Add(
                new("dependency.other", "Other.Package", "1.0", false, false)));
    }

    [Fact]
    public void Model_and_agent_choices_are_independent()
    {
        var modelOnly = Build(ValidInput() with
        {
            Models =
            [
                new("model.qwen", "qwen3:8b", 32_768, true, true),
            ],
            Agents = [],
        });
        var agentOnly = Build(ValidInput() with
        {
            Models = [],
            Agents =
            [
                new(
                    "agent.codex",
                    AgentKind.Codex,
                    AgentIntegrationChoice.InstructionsOnly,
                    null,
                    @"C:\Users\test\.codex\AGENTS.md",
                    true,
                    true),
            ],
        });
        var noChangeAgentWithModel = Build(ValidInput() with
        {
            Models =
            [
                new("model.qwen", "qwen3:8b", 32_768, true, true),
            ],
            Agents =
            [
                new(
                    "agent.codex",
                    AgentKind.Codex,
                    AgentIntegrationChoice.NoChange,
                    null,
                    null,
                    false,
                    false),
            ],
        });

        Assert.Single(modelOnly.Models);
        Assert.Empty(modelOnly.Agents);
        Assert.Empty(agentOnly.Models);
        Assert.Single(agentOnly.Agents);
        Assert.Single(noChangeAgentWithModel.Models);
        Assert.Equal(
            AgentIntegrationChoice.NoChange,
            noChangeAgentWithModel.Agents[0].Choice);
    }

    [Fact]
    public void Duplicate_action_ids_across_categories_are_rejected()
    {
        var input = ValidInput() with
        {
            Dependencies =
            [
                new("shared.action", "Git.Git", "2.50.1", false, false),
            ],
            Models =
            [
                new("shared.action", "qwen3:8b", 32_768, false, false),
            ],
        };

        Assert.Throws<ArgumentException>(() => Build(input));
    }

    [Fact]
    public void Unsupported_diagnosis_is_rejected()
    {
        var input = ValidInput() with
        {
            Diagnosis = SupportedDiagnosis(
                unsupportedReasons: ["Unsupported Windows edition"]),
        };

        Assert.Throws<InvalidOperationException>(() => Build(input));
    }

    [Theory]
    [MemberData(nameof(InvalidInputs))]
    public void Invalid_action_fields_are_rejected(PlanInput input)
    {
        Assert.ThrowsAny<ArgumentException>(() => Build(input));
    }

    [Fact]
    public void Plan_id_and_creation_time_are_deterministic()
    {
        var plan = Build(ValidInput());

        Assert.Equal(ExpectedPlanId, plan.PlanId);
        Assert.Equal(ExpectedCreatedAt, plan.CreatedAtUtc);
    }

    [Fact]
    public void Empty_plan_id_is_rejected()
    {
        var builder = new InstallerPlanBuilder(
            new FixedTimeProvider(ExpectedCreatedAt),
            () => Guid.Empty);

        Assert.Throws<InvalidOperationException>(() => Build(ValidInput(), builder));
    }

    [Fact]
    public void Duplicate_semantic_selections_are_rejected()
    {
        var duplicateDependencies = ValidInput() with
        {
            Dependencies =
            [
                new("dependency.git.1", "Git.Git", "2.50.1", false, false),
                new("dependency.git.2", "git.git", "2.50.2", false, false),
            ],
        };
        var duplicateModels = ValidInput() with
        {
            Models =
            [
                new("model.qwen.1", "qwen3:8b", 32_768, false, false),
                new("model.qwen.2", "QWEN3:8B", 32_768, false, false),
            ],
        };
        var duplicateAgents = ValidInput() with
        {
            Agents =
            [
                new(
                    "agent.codex.1",
                    AgentKind.Codex,
                    AgentIntegrationChoice.NoChange,
                    null,
                    null,
                    false,
                    false),
                new(
                    "agent.codex.2",
                    AgentKind.Codex,
                    AgentIntegrationChoice.NoChange,
                    null,
                    null,
                    false,
                    false),
            ],
        };

        Assert.Throws<ArgumentException>(() => Build(duplicateDependencies));
        Assert.Throws<ArgumentException>(() => Build(duplicateModels));
        Assert.Throws<ArgumentException>(() => Build(duplicateAgents));
    }

    [Fact]
    public void Effects_must_reference_known_consented_external_actions()
    {
        var unknown = ValidInput() with
        {
            Effects =
            [
                new("effect.unknown", "missing.action", "External side effect"),
            ],
        };
        var unselected = ValidInput() with
        {
            Dependencies =
            [
                new("dependency.git", "Git.Git", "2.50.1", false, false),
            ],
            Effects =
            [
                new("effect.git", "dependency.git", "Git remains installed"),
            ],
        };
        var package = ValidInput() with
        {
            Effects =
            [
                new("effect.package", "package.localai", "Not an external effect"),
            ],
        };

        Assert.Throws<ArgumentException>(() => Build(unknown));
        Assert.Throws<InvalidOperationException>(() => Build(unselected));
        Assert.Throws<ArgumentException>(() => Build(package));
    }

    [Fact]
    public void Agent_selection_must_agree_with_no_change_choice()
    {
        var selectedNoChange = ValidInput() with
        {
            Agents =
            [
                new(
                    "agent.codex",
                    AgentKind.Codex,
                    AgentIntegrationChoice.NoChange,
                    null,
                    null,
                    true,
                    true),
            ],
        };
        var unselectedChange = ValidInput() with
        {
            Agents =
            [
                new(
                    "agent.codex",
                    AgentKind.Codex,
                    AgentIntegrationChoice.McpOnly,
                    @"C:\Users\test\.codex\config.toml",
                    null,
                    false,
                    false),
            ],
        };

        Assert.Throws<InvalidOperationException>(() => Build(selectedNoChange));
        Assert.Throws<InvalidOperationException>(() => Build(unselectedChange));
    }

    [Fact]
    public void Effects_for_consented_dependency_and_model_actions_are_accepted()
    {
        var input = ValidInput() with
        {
            Dependencies =
            [
                new("dependency.git", "Git.Git", "2.50.1", true, true),
            ],
            Models =
            [
                new("model.qwen", "qwen3:8b", 32_768, true, true),
            ],
            Effects =
            [
                new("effect.git", "dependency.git", "Git remains installed"),
                new("effect.qwen", "model.qwen", "Model blobs remain downloaded"),
            ],
        };

        var plan = Build(input);

        Assert.Equal(2, plan.NonTransactionalEffects.Count);
    }

    [Theory]
    [InlineData(VersionLikeField.DependencyVersion, "..")]
    [InlineData(VersionLikeField.DependencyVersion, "1..2")]
    [InlineData(VersionLikeField.DependencyVersion, "../escape")]
    [InlineData(VersionLikeField.DependencyVersion, @"1\..\escape")]
    [InlineData(VersionLikeField.PackageVersion, "..")]
    [InlineData(VersionLikeField.PackageVersion, "1..2")]
    [InlineData(VersionLikeField.PackageVersion, "../escape")]
    [InlineData(VersionLikeField.PackageVersion, @"1\..\escape")]
    [InlineData(VersionLikeField.ModelReference, "..")]
    [InlineData(VersionLikeField.ModelReference, "1..2")]
    [InlineData(VersionLikeField.ModelReference, "../escape")]
    [InlineData(VersionLikeField.ModelReference, @"1\..\escape")]
    public void Unsafe_version_like_tokens_are_rejected(
        VersionLikeField field,
        string unsafeValue)
    {
        var input = ValidInput();
        input = field switch
        {
            VersionLikeField.DependencyVersion => input with
            {
                Dependencies =
                [
                    new(
                        "dependency.git",
                        "Git.Git",
                        unsafeValue,
                        false,
                        false),
                ],
            },
            VersionLikeField.PackageVersion => input with
            {
                Package = input.Package with { Version = unsafeValue },
            },
            VersionLikeField.ModelReference => input with
            {
                Models =
                [
                    new(
                        "model.unsafe",
                        unsafeValue,
                        32_768,
                        false,
                        false),
                ],
            },
            _ => throw new ArgumentOutOfRangeException(nameof(field)),
        };

        Assert.ThrowsAny<ArgumentException>(() => Build(input));
    }

    [Fact]
    public void Conservative_version_like_token_boundaries_are_accepted()
    {
        var input = ValidInput() with
        {
            Dependencies =
            [
                new("dependency.git", "Git.Git", "1", false, false),
            ],
            Package = new(
                "package.localai",
                "v1.2.3-rc.1+build_5",
                @"C:\Downloads\localai.zip",
                false,
                false),
            Models =
            [
                new(
                    "model.qwen",
                    "qwen3:8b-q4_K_M",
                    32_768,
                    false,
                    false),
            ],
        };

        var plan = Build(input);

        Assert.Equal("1", plan.Dependencies[0].Version);
        Assert.Equal("v1.2.3-rc.1+build_5", plan.Package.Version);
        Assert.Equal("qwen3:8b-q4_K_M", plan.Models[0].Model);
    }

    [Fact]
    public void Version_like_tokens_accept_the_128_character_boundary()
    {
        var boundaryToken = new string('v', 128);
        var input = ValidInput() with
        {
            Dependencies =
            [
                new("dependency.git", "Git.Git", boundaryToken, false, false),
            ],
            Package = ValidInput().Package with { Version = boundaryToken },
            Models =
            [
                new(
                    "model.boundary",
                    boundaryToken,
                    32_768,
                    false,
                    false),
            ],
        };

        var plan = Build(input);

        Assert.Equal(128, plan.Dependencies[0].Version.Length);
        Assert.Equal(128, plan.Package.Version.Length);
        Assert.Equal(128, plan.Models[0].Model.Length);
    }

    [Fact]
    public void Version_like_tokens_reject_values_over_128_characters()
    {
        var overlongToken = new string('v', 129);
        var dependency = ValidInput() with
        {
            Dependencies =
            [
                new("dependency.git", "Git.Git", overlongToken, false, false),
            ],
        };
        var package = ValidInput() with
        {
            Package = ValidInput().Package with { Version = overlongToken },
        };
        var model = ValidInput() with
        {
            Models =
            [
                new(
                    "model.overlong",
                    overlongToken,
                    32_768,
                    false,
                    false),
            ],
        };

        Assert.Throws<ArgumentException>(() => Build(dependency));
        Assert.Throws<ArgumentException>(() => Build(package));
        Assert.Throws<ArgumentException>(() => Build(model));
    }

    [Fact]
    public void Semantically_duplicate_effects_are_rejected()
    {
        var input = ValidInput() with
        {
            Dependencies =
            [
                new("dependency.git", "Git.Git", "2.50.1", true, true),
            ],
            Effects =
            [
                new(
                    "effect.git.primary",
                    "dependency.git",
                    "Git remains installed"),
                new(
                    "effect.git.duplicate",
                    "DEPENDENCY.GIT",
                    "  git\tREMAINS   installed  "),
            ],
        };

        Assert.Throws<ArgumentException>(() => Build(input));
    }

    [Fact]
    public void Different_effect_descriptions_for_one_action_are_accepted()
    {
        var input = ValidInput() with
        {
            Dependencies =
            [
                new("dependency.git", "Git.Git", "2.50.1", true, true),
            ],
            Effects =
            [
                new(
                    "effect.git.install",
                    "dependency.git",
                    "Git remains installed"),
                new(
                    "effect.git.path",
                    "dependency.git",
                    "Git may update the machine PATH"),
            ],
        };

        var plan = Build(input);

        Assert.Equal(2, plan.NonTransactionalEffects.Count);
    }

    public static TheoryData<PlanInput> InvalidInputs()
    {
        var data = new TheoryData<PlanInput>
        {
            ValidInput() with
            {
                Dependencies =
                [
                    new(" ", "Git.Git", "2.50.1", false, false),
                ],
            },
            ValidInput() with
            {
                Dependencies =
                [
                    new("dependency.git", " ", "2.50.1", false, false),
                ],
            },
            ValidInput() with
            {
                Dependencies =
                [
                    new("dependency.git", "Git.Git", " ", false, false),
                ],
            },
            ValidInput() with
            {
                Package = new(
                    "package.localai",
                    " ",
                    @"C:\Downloads\localai.zip",
                    false,
                    false),
            },
            ValidInput() with
            {
                Package = new(
                    "package.localai",
                    "1.2.3",
                    "relative.zip",
                    false,
                    false),
            },
            ValidInput() with
            {
                Models =
                [
                    new("model.qwen", " ", 32_768, false, false),
                ],
            },
            ValidInput() with
            {
                Models =
                [
                    new("model.qwen", "qwen3:8b", 0, false, false),
                ],
            },
            ValidInput() with
            {
                Agents =
                [
                    new(
                        "agent.codex",
                        AgentKind.Codex,
                        AgentIntegrationChoice.McpOnly,
                        "relative.toml",
                        null,
                        true,
                        true),
                ],
            },
            ValidInput() with
            {
                Effects =
                [
                    new("effect.invalid", " ", "External side effect"),
                ],
            },
            ValidInput() with
            {
                Effects =
                [
                    new("effect.invalid", "dependency.git", " "),
                ],
            },
        };

        return data;
    }

    private static InstallerPlan Build(
        PlanInput input,
        InstallerPlanBuilder? builder = null) =>
        (builder ?? NewBuilder()).Build(
            input.Diagnosis,
            input.Dependencies,
            input.Package,
            input.Models,
            input.Agents,
            input.Effects);

    private static InstallerPlanBuilder NewBuilder() =>
        new(new FixedTimeProvider(ExpectedCreatedAt), () => ExpectedPlanId);

    private static PlanInput ValidInput() =>
        new(
            SupportedDiagnosis(),
            [],
            new(
                "package.localai",
                "1.2.3",
                @"C:\Downloads\localai.zip",
                true,
                true),
            [],
            [],
            []);

    private static EnvironmentDiagnosis SupportedDiagnosis(
        IEnumerable<GpuAdapterSnapshot>? adapters = null,
        IEnumerable<AgentSnapshot>? agents = null,
        IEnumerable<string>? unsupportedReasons = null) =>
        new(
            new OperatingSystemSnapshot(
                "Windows 11 Pro",
                new Version(10, 0, 26100),
                Architecture.X64,
                SupportStatus.Supported,
                SupportStatus.Supported),
            new DiskSnapshot(ObservationState.Available, 100_000, null),
            new NetworkSnapshot(ObservationState.Available, null),
            new DependencySnapshot(
                "WinGet",
                DependencyState.Detected,
                @"C:\Windows\winget.exe",
                "1.10",
                null),
            new DependencySnapshot(
                "Git",
                DependencyState.Detected,
                @"C:\Program Files\Git\bin\git.exe",
                "2.50",
                null),
            new DependencySnapshot(
                "Ollama",
                DependencyState.NotFound,
                null,
                null,
                null),
            new GpuSnapshot(
                ObservationState.Available,
                adapters ?? [new("gpu-1", "GPU 1", 8_000, false)],
                null),
            new ExistingLocalAiSnapshot(
                ExistingLocalAiState.Absent,
                null,
                null,
                null),
            agents ?? [],
            unsupportedReasons ?? []);

    public sealed record PlanInput(
        EnvironmentDiagnosis Diagnosis,
        IReadOnlyList<DependencyAction> Dependencies,
        LocalAiPackageAction Package,
        IReadOnlyList<ModelInstallAction> Models,
        IReadOnlyList<AgentConfigurationAction> Agents,
        IReadOnlyList<NonTransactionalEffect> Effects);

    public enum MutationKind
    {
        Dependency,
        Package,
        Model,
        Agent,
    }

    public enum VersionLikeField
    {
        DependencyVersion,
        PackageVersion,
        ModelReference,
    }

    private sealed class FixedTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => utcNow;
    }
}
