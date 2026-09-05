using System.Text.Json.Serialization;
using LocalAi.Broker.Client;
using LocalAi.Cli.Resources;
using LocalAi.Contracts;
using LocalLm.Core;

namespace LocalAi.Cli;

/// <summary>
/// What a local-model command tells a program: the answer, where it came from, and what the run
/// cost. The rendered notice is not here — it is assembled from a catalogue and would put a
/// sentence that gets reworded onto a versioned wire.
/// </summary>
public sealed record LocalModelData(
    [property: JsonRequired, JsonPropertyName("answer"), JsonPropertyOrder(0)]
    string Answer,
    [property: JsonRequired, JsonPropertyName("origin"), JsonPropertyOrder(1)]
    string Origin,
    [property: JsonRequired, JsonPropertyName("model"), JsonPropertyOrder(2)]
    string Model,
    [property: JsonRequired, JsonPropertyName("residency"), JsonPropertyOrder(3)]
    string Residency,
    [property: JsonRequired, JsonPropertyName("queuedMs"), JsonPropertyOrder(4)]
    long QueuedMs,
    [property: JsonRequired, JsonPropertyName("ranMs"), JsonPropertyOrder(5)]
    long RanMs,
    /// An estimate, and named so. The estimator works from characters, and a caller printing
    /// "27431 tokens saved" from it would be reporting false precision.
    [property: JsonRequired, JsonPropertyName("savedTokensEstimate"), JsonPropertyOrder(6)]
    int SavedTokensEstimate,
    [property: JsonRequired, JsonPropertyName("truncated"), JsonPropertyOrder(7)]
    bool Truncated,
    /// How much of the model was in video memory, when some of it was not. The verdict alone says
    /// a run was degraded; the percentage is what makes it information rather than a warning, and
    /// the prose face has carried it all along. Absent on a healthy run, because a field that is
    /// empty on almost every call teaches a reader to skip it.
    [property: JsonPropertyName("vramResidentPercent"), JsonPropertyOrder(8),
               JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    int? VramResidentPercent = null);

/// <summary>
/// What <c>translate</c> tells a program: the shared block, plus the two figures only this command
/// has.
///
/// No <c>truncated</c> — translation chunks the whole input and drops nothing, so a constant
/// <c>false</c> would imply a concept that does not apply here. No validation verdict either: a
/// failure throws, so on this path it is always "passed" and <c>ok</c> already says so, and its
/// detail is catalogue prose, which is what the notice is kept off the wire for.
/// </summary>
public sealed record TranslationData(
    [property: JsonRequired, JsonPropertyName("answer"), JsonPropertyOrder(0)]
    string Answer,
    [property: JsonRequired, JsonPropertyName("origin"), JsonPropertyOrder(1)]
    string Origin,
    [property: JsonRequired, JsonPropertyName("model"), JsonPropertyOrder(2)]
    string Model,
    [property: JsonRequired, JsonPropertyName("residency"), JsonPropertyOrder(3)]
    string Residency,
    [property: JsonRequired, JsonPropertyName("queuedMs"), JsonPropertyOrder(4)]
    long QueuedMs,
    [property: JsonRequired, JsonPropertyName("ranMs"), JsonPropertyOrder(5)]
    long RanMs,
    /// All three say they are estimates: three bare numbers would invite three false precisions
    /// where one would have done.
    [property: JsonRequired, JsonPropertyName("savedTokensEstimate"), JsonPropertyOrder(6)]
    int SavedTokensEstimate,
    [property: JsonRequired, JsonPropertyName("localTokensProcessedEstimate"),
               JsonPropertyOrder(7)]
    int LocalTokensProcessedEstimate,
    [property: JsonRequired, JsonPropertyName("netContextTokensSavedEstimate"),
               JsonPropertyOrder(8)]
    int NetContextTokensSavedEstimate,
    [property: JsonPropertyName("vramResidentPercent"), JsonPropertyOrder(9),
               JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    int? VramResidentPercent = null);

/// <summary>
/// The half of a local-model command that is the same for all of them: run the task, put the
/// answer and the notice where each belongs, and turn a failure into a code and an exit.
///
/// Deliberately not <c>LocalLmTools.Run</c>. That one converts every failure into readable text
/// and returns it, which is right for MCP — its own comment records that a protocol error made a
/// missing file look like a broken tool — and wrong here, because a console has exit codes and a
/// script has to be able to tell "no model installed" from "the model failed".
/// </summary>
internal static class LocalModelRun
{
    public static async Task<int> ExecuteAsync(
        string command,
        string origin,
        LocalTaskProfile profile,
        Func<LocalTasks, CancellationToken, Task<LocalResult>> job,
        bool machineReadable,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(job);
        try
        {
            var result = await job(Tasks(Watching()), cancellationToken);
            if (machineReadable)
            {
                Console.WriteLine(MachineOutput.Answer(command, Describe(origin, result)));
                return 0;
            }

            // The notice is about the run and the answer is the product, so
            // `localai ask "…" x.cs > out.md` leaves a file with the answer and nothing else.
            // `hook` already puts its own synchronization line on stderr for this reason.
            Console.Error.WriteLine(result.Notice);
            Console.WriteLine(LocalModelOutput.Answer(
                origin,
                result.Answer,
                Console.IsOutputRedirected));
            return 0;
        }
        catch (Exception exception) when (Classify(exception, profile) is not null)
        {
            var (code, message, exit) = Classify(exception, profile)!.Value;
            return Fail(command, code, message, exit, machineReadable);
        }
    }

    /// <summary>
    /// Translation, which differs enough to need its own path: a result with three token figures,
    /// no model override, and an answer that is a document rather than a statement about one.
    /// The failure ladder is shared, so a caller never has to learn two.
    /// </summary>
    public static async Task<int> TranslateAsync(
        string origin,
        TranslateRequest request,
        string text,
        bool machineReadable,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        try
        {
            var result = await Tasks(Watching()).TranslateAsync(
                text,
                request.From,
                request.To,
                request.Markdown,
                cancellationToken);

            if (request.OutputPath is { } path)
            {
                try
                {
                    // The document itself, with no provenance markers around it. That is what
                    // `--out` is for: a file wrapped in them would not be a document.
                    await File.WriteAllTextAsync(path, result.Answer, cancellationToken);
                }
                catch (Exception failure) when (
                    failure is IOException or UnauthorizedAccessException or NotSupportedException)
                {
                    // 73 is EX_CANTCREAT. Not 2 — the command line was right, and the cause is a
                    // permission or a directory that is not there.
                    return Fail(
                        "translate",
                        "output_not_written",
                        CliText.OutputNotWritten(path, failure.Message),
                        73,
                        machineReadable);
                }
            }

            if (machineReadable)
            {
                Console.WriteLine(MachineOutput.Answer("translate", Describe(origin, result)));
                return 0;
            }

            Console.Error.WriteLine(result.Notice);
            if (request.OutputPath is null)
            {
                if (Console.IsOutputRedirected)
                {
                    // Discoverability is what `--out` costs, and this line is what pays it: the
                    // reader is otherwise about to save a document full of markers.
                    Console.Error.WriteLine(CliText.TranslateWriteDocument);
                }

                Console.WriteLine(LocalModelOutput.Answer(
                    origin,
                    result.Answer,
                    Console.IsOutputRedirected));
            }

            return 0;
        }
        catch (InvalidDataException malformed)
        {
            // The validator checks structure — fences, placeholders, counts, prompt leak — so it
            // is the output's form that failed, which is why this is not called "invalid". Exit 65
            // is the one `chunk_rejected` uses: a model produced data this cannot use.
            return Fail(
                "translate",
                "translation_malformed",
                malformed.Message,
                65,
                machineReadable);
        }
        catch (Exception exception) when (
            Classify(exception, LocalTaskProfile.PlainTranslation) is not null)
        {
            var (code, message, exit) =
                Classify(exception, LocalTaskProfile.PlainTranslation)!.Value;
            return Fail("translate", code, message, exit, machineReadable);
        }
    }

    /// <summary>
    /// One observer for the whole run, given to both layers: the broker client knows
    /// whether the job is queued or running, and the task knows which fragment it is on.
    /// Neither knows what the other is doing, and the console needs both to say anything
    /// true about a long run.
    /// </summary>
    private static LocalTasks Tasks(ILocalRunObserver observer) =>
        new(
            new BrokerLocalModelClient(
                BrokerClientFactory.CreateDefault(observer: observer)),
            observer);

    /// <summary>
    /// Built at the moment the run starts, because its first decision is measured from
    /// then: ten seconds of a run that has said nothing earns the first line.
    /// </summary>
    private static LocalRunProgress Watching() =>
        new(Console.Error, static () => DateTimeOffset.UtcNow);

    /// <summary>
    /// One ladder for every local task in this binary. The MCP face has its own, which turns each
    /// failure into readable text and returns it — right for a protocol with no exit codes — but
    /// here there is exactly one, so two commands cannot classify the same failure differently.
    ///
    /// Anything it does not recognise is left to the entry point's guard, which is where an
    /// unexpected failure belongs.
    /// </summary>
    private static (string Code, string Message, int Exit)? Classify(
        Exception exception,
        LocalTaskProfile profile) => exception switch
        {
            // 69 is EX_UNAVAILABLE: a service this needs is not there. Not 2, which would send
            // somebody to fix a command line that is correct, and not 75, which says try again
            // later — a model that is not installed does not install itself. The message names
            // the command that installs one.
            BrokerJobFailedException { FailureCode: "NoModelInstalledException" } =>
                ("model_not_installed", MissingModelAdvice.ForProfile(profile), 69),
            BrokerJobFailedException { FailureCode: "NoEligibleModelException" } =>
                ("model_not_eligible", MissingModelAdvice.ForIneligibleRequest(profile), 69),
            // `FailureCode` is an exception type name — PascalCase, and an internal one — so it
            // goes in the message a person reads and never in the code a program switches on.
            BrokerJobFailedException failed => ("local_model_failed", failed.Message, 70),
            // These are already tokens in this vocabulary — broker_start_timeout,
            // broker_executable_missing — so four distinguishable failures stay four.
            BrokerBootstrapException bootstrap => (bootstrap.Code, bootstrap.Message, 75),
            FileNotFoundException missing => ("file_missing", missing.Message, 2),
            // Before ArgumentException, which it derives from: a profile the task will not run is
            // not the same failure as a bound that was exceeded.
            ArgumentOutOfRangeException rejectedProfile =>
                ("profile_not_supported", rejectedProfile.Message, 2),
            // Every bound a task enforces — too many files, an image too large, a question that
            // will not fit the context — arrives as this one type carrying a catalogue sentence.
            // Splitting them into codes of their own needs typed exceptions in LocalLm.Core.
            ArgumentException rejected => ("input_rejected", rejected.Message, 2),
            _ => null,
        };

    private static LocalModelData Describe(string origin, LocalResult result) =>
        new(
            result.Answer,
            origin,
            result.Model,
            (result.Receipt.Routing?.ResidencyShortfall ?? ResidencyShortfall.None).ToString(),
            (long)result.Receipt.QueueDuration.TotalMilliseconds,
            (long)result.Receipt.ExecutionDuration.TotalMilliseconds,
            result.SavedTokens,
            result.Truncated,
            Vram(result.Receipt));

    private static TranslationData Describe(string origin, LocalTranslationResult result) =>
        new(
            result.Answer,
            origin,
            result.Model,
            (result.Receipt.Routing?.ResidencyShortfall ?? ResidencyShortfall.None).ToString(),
            (long)result.Receipt.QueueDuration.TotalMilliseconds,
            (long)result.Receipt.ExecutionDuration.TotalMilliseconds,
            result.SavedTokens,
            result.LocalTokensProcessed,
            result.NetCloudContextTokensSaved,
            Vram(result.Receipt));

    private static int? Vram(LocalUsageReceipt receipt) =>
        receipt.Routing?.ResidencyShortfall is ResidencyShortfall.None or null
            ? null
            : receipt.Routing?.VramResidentPercent;

    private static int Fail(
        string command,
        string code,
        string message,
        int exitCode,
        bool machineReadable)
    {
        if (machineReadable)
        {
            Console.WriteLine(MachineOutput.Refusal(command, code, message));
        }
        else
        {
            Console.Error.WriteLine(message);
        }

        return exitCode;
    }
}
