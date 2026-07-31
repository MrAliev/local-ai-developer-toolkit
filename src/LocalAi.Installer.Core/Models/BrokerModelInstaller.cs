using System.Collections.ObjectModel;
using System.Runtime.Versioning;
using System.Text.Json;
using LocalAi.Contracts;
using LocalAi.Installer.Core.Abstractions;
using LocalAi.Installer.Core.Activation;
using LocalAi.Installer.Core.Planning;
using LocalAi.Installer.Core.Releases;

namespace LocalAi.Installer.Core.Models;

internal interface ITrustedStableLauncher
{
    string CanonicalPath { get; }

    void Revalidate();
}

public sealed class BrokerModelInstallRequest
{
    public BrokerModelInstallRequest(
        ModelInstallAction action,
        string catalogVersion,
        IReadOnlyList<ModelRecommendationChoice> fallbackChoices)
    {
        Action = action ?? throw new ArgumentNullException(nameof(action));
        CatalogVersion = catalogVersion ?? throw new ArgumentNullException(nameof(catalogVersion));
        FallbackChoices = new ReadOnlyCollection<ModelRecommendationChoice>(
            (fallbackChoices ?? throw new ArgumentNullException(nameof(fallbackChoices))).ToArray());
    }

    public ModelInstallAction Action { get; }

    public string CatalogVersion { get; }

    public IReadOnlyList<ModelRecommendationChoice> FallbackChoices { get; }
}

public enum BrokerModelInstallOutcome
{
    Accepted,
    RejectedResidency,
    Failed,
    Cancelled,
    TimedOut,
}

public enum BrokerModelBatchStopReason
{
    None,
    Cancelled,
    TimedOut,
    ProcessFailure,
    ProtocolFailure,
    LauncherTrustFailure,
}

public sealed record ModelFallbackSuggestion(
    string Model,
    int ContextTokens,
    ulong RequiredVramBytes);

public sealed class BrokerModelInstallResult
{
    internal BrokerModelInstallResult(
        string actionId,
        string model,
        int contextTokens,
        BrokerModelInstallOutcome outcome,
        bool pullAttempted,
        bool pullCompleted,
        bool externalStateMayBeIndeterminate,
        IReadOnlyList<ModelFallbackSuggestion> fallbackSuggestions,
        string code)
    {
        ActionId = actionId;
        Model = model;
        ContextTokens = contextTokens;
        Outcome = outcome;
        PullAttempted = pullAttempted;
        PullCompleted = pullCompleted;
        ExternalStateMayBeIndeterminate = externalStateMayBeIndeterminate;
        FallbackSuggestions = new ReadOnlyCollection<ModelFallbackSuggestion>(
            fallbackSuggestions.ToArray());
        NoFallbackAvailable = outcome == BrokerModelInstallOutcome.RejectedResidency &&
            FallbackSuggestions.Count == 0;
        Code = code;
    }

    public string ActionId { get; }
    public string Model { get; }
    public int ContextTokens { get; }
    public BrokerModelInstallOutcome Outcome { get; }
    public bool PullAttempted { get; }
    public bool PullCompleted { get; }
    public bool ExternalStateMayBeIndeterminate { get; }
    public IReadOnlyList<ModelFallbackSuggestion> FallbackSuggestions { get; }
    public bool NoFallbackAvailable { get; }
    public string Code { get; }
}

public sealed class BrokerModelInstallBatchResult
{
    internal BrokerModelInstallBatchResult(
        IReadOnlyList<BrokerModelInstallResult> models,
        BrokerModelBatchStopReason stopReason,
        bool externalStateMayBeIndeterminate,
        string code)
    {
        Models = new ReadOnlyCollection<BrokerModelInstallResult>(models.ToArray());
        StopReason = stopReason;
        ExternalStateMayBeIndeterminate = externalStateMayBeIndeterminate;
        Code = code;
    }

    public IReadOnlyList<BrokerModelInstallResult> Models { get; }
    public BrokerModelBatchStopReason StopReason { get; }
    public bool ExternalStateMayBeIndeterminate { get; }
    public string Code { get; }
}

public sealed class BrokerModelInstaller : IDisposable
{
    private const int MaximumResponseCharacters = 65_536;
    private const int MaximumStatusModels = 256;
    private static readonly TimeSpan MaximumCommandTimeout = TimeSpan.FromMinutes(30);
    private readonly IProcessRunner processRunner;
    private readonly ITrustedStableLauncher launcher;
    private readonly string launcherPath;
    private readonly TimeSpan timeout;
    private readonly IDisposable? ownedLauncher;
    private bool disposed;

    internal BrokerModelInstaller(
        IProcessRunner processRunner,
        ITrustedStableLauncher launcher,
        InstallationLayout layout,
        TimeSpan timeout)
    {
        this.processRunner = processRunner ??
            throw new ArgumentNullException(nameof(processRunner));
        this.launcher = launcher ?? throw new ArgumentNullException(nameof(launcher));
        ArgumentNullException.ThrowIfNull(layout);
        if (timeout <= TimeSpan.Zero || timeout > MaximumCommandTimeout)
        {
            throw new ArgumentOutOfRangeException(nameof(timeout));
        }

        launcherPath = ValidateLauncherPath(launcher, layout);
        this.timeout = timeout;
    }

    [SupportedOSPlatform("windows")]
    public BrokerModelInstaller(
        IProcessRunner processRunner,
        InstallationLayoutLease layoutLease,
        VerifiedPackageFile launcherMetadata,
        TimeSpan timeout)
    {
        this.processRunner = processRunner ??
            throw new ArgumentNullException(nameof(processRunner));
        ArgumentNullException.ThrowIfNull(layoutLease);
        ArgumentNullException.ThrowIfNull(launcherMetadata);
        if (!string.Equals(
                launcherMetadata.RelativePath,
                LocalAiPackageLayout.StableLauncherFile,
                StringComparison.Ordinal))
        {
            throw new ArgumentException(
                "The verified metadata is not for the stable launcher.",
                nameof(launcherMetadata));
        }

        if (timeout <= TimeSpan.Zero || timeout > MaximumCommandTimeout)
        {
            throw new ArgumentOutOfRangeException(nameof(timeout));
        }

        var retained = layoutLease.LockLauncher(launcherMetadata);
        var adapter = new TaskSixTrustedLauncher(retained);
        try
        {
            launcher = adapter;
            launcherPath = ValidateLauncherPath(adapter, layoutLease.Layout);
            this.timeout = timeout;
            ownedLauncher = adapter;
        }
        catch
        {
            adapter.Dispose();
            throw;
        }
    }

    public async Task<BrokerModelInstallBatchResult> InstallAsync(
        IReadOnlyList<BrokerModelInstallRequest> requests,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        var snapshot = ValidateAndSnapshot(requests);
        if (snapshot.Length == 0)
        {
            return Batch([], BrokerModelBatchStopReason.None, false, "none");
        }

        if (cancellationToken.IsCancellationRequested)
        {
            return Batch([], BrokerModelBatchStopReason.Cancelled, false, "cancelled");
        }

        ModelStatusCommandSuccess status;
        try
        {
            var process = await RunAsync(
                ["run", "localai", "model", "status"],
                mayChangeExternalState: false,
                cancellationToken);
            EnsureExitSuccess(process, "status", mayChangeExternalState: false);
            status = Parse<ModelStatusCommandSuccess>(
                process.StandardOutput,
                indeterminateIfInvalid: false);
            ValidateStatus(status, snapshot[0].CatalogVersion);
        }
        catch (CommandFailure failure)
        {
            return Batch([], failure.StopReason, failure.Indeterminate, failure.Code);
        }

        var installed = status.InstalledModels.ToHashSet(StringComparer.Ordinal);
        var pending = status.PendingPullModels.ToHashSet(StringComparer.Ordinal);
        var results = new List<BrokerModelInstallResult>(snapshot.Length);

        foreach (var request in snapshot)
        {
            var action = request.Action;
            var pullAttempted = false;
            var pullCompleted = false;
            try
            {
                if (!installed.Contains(action.Model) && !pending.Contains(action.Model))
                {
                    pullAttempted = true;
                    var pull = await RunAsync(
                        [
                            "run", "localai", "model", "pull",
                            "--model", action.Model,
                            "--catalog-version", request.CatalogVersion,
                        ],
                        mayChangeExternalState: true,
                        cancellationToken);
                    EnsureExitSuccess(pull, "pull", mayChangeExternalState: true);
                    ValidatePull(
                        Parse<ModelPullCommandSuccess>(
                            pull.StandardOutput,
                            indeterminateIfInvalid: true),
                        action.Model,
                        request.CatalogVersion);
                    pullCompleted = true;
                }

                var preflight = await RunAsync(
                    [
                        "run", "localai", "model", "preflight",
                        "--model", action.Model,
                        "--context", action.ContextSize.ToString(
                            System.Globalization.CultureInfo.InvariantCulture),
                    ],
                    mayChangeExternalState: true,
                    cancellationToken);
                if (preflight.ExitCode == 3)
                {
                    var rejected = Parse<ModelPreflightCommandRejected>(
                        preflight.StandardOutput,
                        indeterminateIfInvalid: true);
                    ValidateRejection(rejected, action.Model, action.ContextSize);
                    results.Add(Rejected(request, pullAttempted, pullCompleted));
                    continue;
                }

                EnsureExitSuccess(
                    preflight,
                    "preflight",
                    mayChangeExternalState: true);
                var proof = Parse<ModelPreflightCommandSuccess>(
                    preflight.StandardOutput,
                    indeterminateIfInvalid: true);
                ValidateProofIdentity(proof, action.Model, action.ContextSize);
                if (proof.SizeBytes <= 0 ||
                    proof.SizeVramBytes != proof.SizeBytes ||
                    !proof.FullyResident)
                {
                    results.Add(Rejected(request, pullAttempted, pullCompleted));
                    continue;
                }

                results.Add(new BrokerModelInstallResult(
                    action.ActionId,
                    action.Model,
                    action.ContextSize,
                    BrokerModelInstallOutcome.Accepted,
                    pullAttempted,
                    pullCompleted,
                    false,
                    [],
                    "accepted"));
            }
            catch (CommandFailure failure)
            {
                results.Add(new BrokerModelInstallResult(
                    action.ActionId,
                    action.Model,
                    action.ContextSize,
                    ToOutcome(failure.StopReason),
                    pullAttempted,
                    pullCompleted,
                    failure.Indeterminate,
                    [],
                    failure.Code));
                return Batch(
                    results,
                    failure.StopReason,
                    failure.Indeterminate,
                    failure.Code);
            }
        }

        return Batch(results, BrokerModelBatchStopReason.None, false, "none");
    }

    public void Dispose()
    {
        if (!disposed)
        {
            disposed = true;
            ownedLauncher?.Dispose();
        }
    }

    private async Task<ProcessResult> RunAsync(
        IReadOnlyList<string> arguments,
        bool mayChangeExternalState,
        CancellationToken cancellationToken)
    {
        try
        {
            launcher.Revalidate();
        }
        catch (Exception)
        {
            throw Failure(
                BrokerModelBatchStopReason.LauncherTrustFailure,
                false,
                "launcher_trust_failure");
        }

        ProcessResult? process = null;
        CommandFailure? processFailure = null;
        try
        {
            process = await processRunner.RunAsync(
                launcherPath,
                arguments,
                timeout,
                cancellationToken);
        }
        catch (ProcessTerminationException exception)
        {
            processFailure = Failure(
                exception.Cause == ProcessTerminationCause.Timeout
                    ? BrokerModelBatchStopReason.TimedOut
                    : BrokerModelBatchStopReason.Cancelled,
                mayChangeExternalState,
                exception.Cause == ProcessTerminationCause.Timeout
                    ? "process_termination_timeout"
                    : "process_termination_cancelled");
        }
        catch (OperationCanceledException)
        {
            processFailure = Failure(
                BrokerModelBatchStopReason.Cancelled,
                mayChangeExternalState,
                "cancelled");
        }
        catch (Exception)
        {
            processFailure = Failure(
                BrokerModelBatchStopReason.ProcessFailure,
                mayChangeExternalState,
                "process_failure");
        }

        try
        {
            launcher.Revalidate();
        }
        catch (Exception)
        {
            throw Failure(
                BrokerModelBatchStopReason.LauncherTrustFailure,
                mayChangeExternalState,
                "launcher_trust_failure");
        }

        if (processFailure is not null)
        {
            throw processFailure;
        }

        if (process is null)
        {
            throw Failure(
                BrokerModelBatchStopReason.ProcessFailure,
                mayChangeExternalState,
                "process_failure");
        }

        if (process.Cancelled)
        {
            throw Failure(
                BrokerModelBatchStopReason.Cancelled,
                mayChangeExternalState,
                "cancelled");
        }

        if (process.TimedOut)
        {
            throw Failure(
                BrokerModelBatchStopReason.TimedOut,
                mayChangeExternalState,
                "timed_out");
        }

        if (process.StandardOutputTruncated ||
            process.StandardErrorTruncated ||
            process.StandardOutput.Length > MaximumResponseCharacters ||
            process.StandardError.Length != 0)
        {
            throw Failure(
                BrokerModelBatchStopReason.ProtocolFailure,
                mayChangeExternalState,
                "response_invalid");
        }

        return process;
    }

    private static void EnsureExitSuccess(
        ProcessResult process,
        string operation,
        bool mayChangeExternalState)
    {
        if (process.ExitCode == 0)
        {
            return;
        }

        if (process.ExitCode == 4)
        {
            ValidateError(
                process.StandardOutput,
                operation,
                "cancelled",
                mayChangeExternalState);
            throw Failure(
                BrokerModelBatchStopReason.Cancelled,
                mayChangeExternalState,
                "cancelled");
        }

        if (process.ExitCode == 1)
        {
            ValidateError(
                process.StandardOutput,
                operation,
                "broker_failure",
                mayChangeExternalState);
            throw Failure(
                BrokerModelBatchStopReason.ProcessFailure,
                mayChangeExternalState,
                "broker_failure");
        }

        throw Failure(
            BrokerModelBatchStopReason.ProtocolFailure,
            mayChangeExternalState,
            "exit_code_invalid");
    }

    private static void ValidateError(
        string standardOutput,
        string operation,
        string code,
        bool indeterminateIfInvalid)
    {
        var response = Parse<ModelCommandError>(
            standardOutput,
            indeterminateIfInvalid);
        if (response.SchemaVersion != 1 || response.Accepted ||
            !string.Equals(response.Operation, operation, StringComparison.Ordinal) ||
            !string.Equals(response.Code, code, StringComparison.Ordinal))
        {
            throw Failure(
                BrokerModelBatchStopReason.ProtocolFailure,
                false,
                "response_invalid");
        }
    }

    private static void ValidateStatus(
        ModelStatusCommandSuccess response,
        string catalogVersion)
    {
        if (response.SchemaVersion != 1 || !response.Accepted ||
            !string.Equals(response.Operation, "status", StringComparison.Ordinal) ||
            !string.Equals(response.CatalogVersion, catalogVersion, StringComparison.Ordinal) ||
            !ValidModelList(response.InstalledModels) ||
            !ValidModelList(response.PendingPullModels) ||
            response.InstalledModels.Intersect(
                response.PendingPullModels,
                StringComparer.Ordinal).Any())
        {
            throw Failure(
                BrokerModelBatchStopReason.ProtocolFailure,
                false,
                "response_invalid");
        }
    }

    private static void ValidatePull(
        ModelPullCommandSuccess response,
        string model,
        string catalogVersion)
    {
        if (response.SchemaVersion != 1 || !response.Accepted ||
            !string.Equals(response.Operation, "pull", StringComparison.Ordinal) ||
            !string.Equals(response.Model, model, StringComparison.Ordinal) ||
            !string.Equals(
                response.CatalogVersion,
                catalogVersion,
                StringComparison.Ordinal) ||
            !string.Equals(response.Status, "success", StringComparison.Ordinal))
        {
            throw Failure(
                BrokerModelBatchStopReason.ProtocolFailure,
                true,
                "response_invalid");
        }
    }

    private static void ValidateProofIdentity(
        ModelPreflightCommandSuccess response,
        string model,
        int contextTokens)
    {
        if (response.SchemaVersion != 1 || !response.Accepted ||
            !string.Equals(response.Operation, "preflight", StringComparison.Ordinal) ||
            !string.Equals(response.Model, model, StringComparison.Ordinal) ||
            response.ContextTokens != contextTokens ||
            response.VerifiedAtUtc == default ||
            response.VerifiedAtUtc.Offset != TimeSpan.Zero)
        {
            throw Failure(
                BrokerModelBatchStopReason.ProtocolFailure,
                true,
                "response_invalid");
        }
    }

    private static void ValidateRejection(
        ModelPreflightCommandRejected response,
        string model,
        int contextTokens)
    {
        if (response.SchemaVersion != 1 || response.Accepted ||
            !string.Equals(response.Operation, "preflight", StringComparison.Ordinal) ||
            !string.Equals(response.Model, model, StringComparison.Ordinal) ||
            response.ContextTokens != contextTokens ||
            !string.Equals(
                response.Code,
                "residency_rejected",
                StringComparison.Ordinal))
        {
            throw Failure(
                BrokerModelBatchStopReason.ProtocolFailure,
                true,
                "response_invalid");
        }
    }

    private static T Parse<T>(
        string standardOutput,
        bool indeterminateIfInvalid)
    {
        try
        {
            var json = SingleJsonLine(standardOutput);
            return JsonSerializer.Deserialize<T>(json, LocalAiJson.Strict)
                ?? throw new JsonException();
        }
        catch (Exception exception) when (
            exception is JsonException or NotSupportedException or ArgumentException)
        {
            throw Failure(
                BrokerModelBatchStopReason.ProtocolFailure,
                indeterminateIfInvalid,
                "response_invalid");
        }
    }

    private static string SingleJsonLine(string value)
    {
        if (string.IsNullOrEmpty(value) || value.Length > MaximumResponseCharacters)
        {
            throw new JsonException();
        }

        var json = value.EndsWith("\r\n", StringComparison.Ordinal)
            ? value[..^2]
            : value.EndsWith('\n')
                ? value[..^1]
                : value;
        if (json.Length == 0 || json[0] != '{' || json[^1] != '}' ||
            json.Contains('\r') || json.Contains('\n'))
        {
            throw new JsonException();
        }

        return json;
    }

    private static BrokerModelInstallRequest[] ValidateAndSnapshot(
        IReadOnlyList<BrokerModelInstallRequest> requests)
    {
        ArgumentNullException.ThrowIfNull(requests);
        var snapshot = requests.ToArray();
        if (snapshot.Any(request => request is null))
        {
            throw new ArgumentException("A model request cannot be null.", nameof(requests));
        }

        foreach (var request in snapshot)
        {
            var action = request.Action;
            if (!action.Selected || !action.ConsentGranted ||
                !IsSafeIdentifier(action.ActionId) ||
                !IsSafeModel(action.Model) ||
                !LocalContextTiers.IsSupported(action.ContextSize) ||
                !IsSafeCatalogVersion(request.CatalogVersion))
            {
                throw new ArgumentException("A model request is invalid.", nameof(requests));
            }

            ValidateChoices(request);
        }

        if (snapshot.Select(request => request.Action.ActionId)
                .Distinct(StringComparer.Ordinal).Count() != snapshot.Length ||
            snapshot.Select(request =>
                    (request.Action.Model, request.Action.ContextSize))
                .Distinct().Count() != snapshot.Length ||
            snapshot.Select(request => request.CatalogVersion)
                .Distinct(StringComparer.Ordinal).Count() > 1)
        {
            throw new ArgumentException(
                "Model requests contain duplicates or mixed catalog versions.",
                nameof(requests));
        }

        return snapshot;
    }

    private static void ValidateChoices(BrokerModelInstallRequest request)
    {
        if (request.FallbackChoices.Count == 0 ||
            request.FallbackChoices.Any(choice =>
                choice is null ||
                !IsSafeModel(choice.Name) ||
                !LocalContextTiers.IsSupported(choice.ContextTokens)) ||
            request.FallbackChoices.Select(choice =>
                    (choice.Name, choice.ContextTokens))
                .Distinct().Count() != request.FallbackChoices.Count)
        {
            throw new ArgumentException("Model recommendation choices are invalid.");
        }

        var selected = request.FallbackChoices.SingleOrDefault(choice =>
            string.Equals(
                choice.Name,
                request.Action.Model,
                StringComparison.Ordinal) &&
            choice.ContextTokens == request.Action.ContextSize);
        if (selected is null || !selected.IsEnabled ||
            selected.OverBudgetBytes != 0 ||
            selected.RequiredBytes > selected.AvailableDedicatedBytes)
        {
            throw new ArgumentException(
                "The selected model is not an enabled recommendation choice.");
        }
    }

    private static bool ValidModelList(IReadOnlyList<string>? models) =>
        models is not null &&
        models.Count <= MaximumStatusModels &&
        models.All(IsSafeModel) &&
        models.Distinct(StringComparer.Ordinal).Count() == models.Count;

    private static bool IsSafeIdentifier(string? value) =>
        IsSafeAsciiToken(value, 128, allowColon: false, allowSlash: false);

    private static bool IsSafeCatalogVersion(string? value) =>
        IsSafeAsciiToken(value, 128, allowColon: false, allowSlash: false);

    private static bool IsSafeModel(string? value) =>
        IsSafeAsciiToken(value, 200, allowColon: true, allowSlash: true) &&
        !value!.Contains("..", StringComparison.Ordinal) &&
        !value.Contains("//", StringComparison.Ordinal) &&
        !value.Contains("::", StringComparison.Ordinal) &&
        !value.Contains("/:", StringComparison.Ordinal) &&
        !value.Contains(":/", StringComparison.Ordinal);

    private static bool IsSafeAsciiToken(
        string? value,
        int maximumLength,
        bool allowColon,
        bool allowSlash)
    {
        if (string.IsNullOrEmpty(value) || value.Length > maximumLength ||
            !IsAsciiAlphaNumeric(value[0]) ||
            !IsAsciiAlphaNumeric(value[^1]))
        {
            return false;
        }

        return value.All(character =>
            IsAsciiAlphaNumeric(character) ||
            character is '.' or '_' or '-' ||
            (allowColon && character == ':') ||
            (allowSlash && character == '/'));
    }

    private static bool IsAsciiAlphaNumeric(char value) =>
        value is >= 'a' and <= 'z' or >= 'A' and <= 'Z' or >= '0' and <= '9';

    private static string ValidateLauncherPath(
        ITrustedStableLauncher launcher,
        InstallationLayout layout)
    {
        string expectedPath;
        string suppliedPath;
        string actualPath;
        try
        {
            expectedPath = Path.GetFullPath(layout.LauncherPath);
            suppliedPath = launcher.CanonicalPath;
            actualPath = Path.GetFullPath(suppliedPath);
        }
        catch (Exception exception) when (
            exception is ArgumentException or NotSupportedException or
            PathTooLongException)
        {
            throw new ArgumentException(
                "The trusted launcher path is invalid.",
                nameof(launcher));
        }

        if (!Path.IsPathFullyQualified(suppliedPath) ||
            !string.Equals(suppliedPath, actualPath, PathComparison) ||
            !string.Equals(expectedPath, actualPath, PathComparison))
        {
            throw new ArgumentException(
                "The trusted launcher is not the exact stable launcher path.",
                nameof(launcher));
        }

        return actualPath;
    }

    private static BrokerModelInstallResult Rejected(
        BrokerModelInstallRequest request,
        bool pullAttempted,
        bool pullCompleted)
    {
        var action = request.Action;
        var selected = request.FallbackChoices.Single(choice =>
            string.Equals(choice.Name, action.Model, StringComparison.Ordinal) &&
            choice.ContextTokens == action.ContextSize);
        var suggestions = request.FallbackChoices
            .Where(choice =>
                choice.IsEnabled &&
                choice.OverBudgetBytes == 0 &&
                choice.RequiredBytes <= choice.AvailableDedicatedBytes &&
                !(string.Equals(choice.Name, action.Model, StringComparison.Ordinal) &&
                    choice.ContextTokens == action.ContextSize) &&
                ((string.Equals(choice.Name, action.Model, StringComparison.Ordinal) &&
                    choice.ContextTokens < action.ContextSize) ||
                    choice.RequiredBytes < selected.RequiredBytes))
            .Select(choice => new ModelFallbackSuggestion(
                choice.Name,
                choice.ContextTokens,
                choice.RequiredBytes))
            .ToArray();
        return new BrokerModelInstallResult(
            action.ActionId,
            action.Model,
            action.ContextSize,
            BrokerModelInstallOutcome.RejectedResidency,
            pullAttempted,
            pullCompleted,
            false,
            suggestions,
            "residency_rejected");
    }

    private static BrokerModelInstallOutcome ToOutcome(
        BrokerModelBatchStopReason stopReason) =>
        stopReason switch
        {
            BrokerModelBatchStopReason.Cancelled => BrokerModelInstallOutcome.Cancelled,
            BrokerModelBatchStopReason.TimedOut => BrokerModelInstallOutcome.TimedOut,
            _ => BrokerModelInstallOutcome.Failed,
        };

    private static BrokerModelInstallBatchResult Batch(
        IReadOnlyList<BrokerModelInstallResult> models,
        BrokerModelBatchStopReason stopReason,
        bool indeterminate,
        string code) =>
        new(models, stopReason, indeterminate, code);

    private static CommandFailure Failure(
        BrokerModelBatchStopReason stopReason,
        bool indeterminate,
        string code) =>
        new(stopReason, indeterminate, code);

    private static StringComparison PathComparison => OperatingSystem.IsWindows()
        ? StringComparison.OrdinalIgnoreCase
        : StringComparison.Ordinal;

    [SupportedOSPlatform("windows")]
    private sealed class TaskSixTrustedLauncher(
        InstallationLayoutLease.TrustedLauncher launcher) :
        ITrustedStableLauncher,
        IDisposable
    {
        private readonly InstallationLayoutLease.TrustedLauncher launcher =
            launcher ?? throw new ArgumentNullException(nameof(launcher));

        public string CanonicalPath => launcher.CanonicalPath;

        public void Revalidate() => launcher.Revalidate();

        public void Dispose() => launcher.Dispose();
    }

    private sealed class CommandFailure(
        BrokerModelBatchStopReason stopReason,
        bool indeterminate,
        string code) : Exception
    {
        public BrokerModelBatchStopReason StopReason { get; } = stopReason;
        public bool Indeterminate { get; } = indeterminate;
        public string Code { get; } = code;
    }
}
