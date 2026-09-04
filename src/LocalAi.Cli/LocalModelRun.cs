using System.Text.Json.Serialization;
using LocalAi.Broker.Client;
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
    /// How much of the model was in video memory, when some of it was not. The verdict
    /// alone says a run was degraded; the percentage is what makes it information rather
    /// than a warning, and the prose face has carried it all along. Absent on a healthy
    /// run, because a field that is empty on almost every call teaches a reader to skip it.
    [property: JsonPropertyName("vramResidentPercent"), JsonPropertyOrder(8),
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
        try
        {
            var tasks = new LocalTasks(
                new BrokerLocalModelClient(BrokerClientFactory.CreateDefault()));
            var result = await job(tasks, cancellationToken);
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
        catch (BrokerJobFailedException failure) when (
            failure.FailureCode.Equals("NoModelInstalledException", StringComparison.Ordinal))
        {
            // 69 is EX_UNAVAILABLE: a service this needs is not there. Not 2, which would send
            // somebody to fix a command line that is correct, and not 75, which says try again
            // later — a model that is not installed does not install itself. The message names
            // the command that installs one.
            return Fail(
                command,
                "model_not_installed",
                MissingModelAdvice.ForProfile(profile),
                69,
                machineReadable);
        }
        catch (BrokerJobFailedException failure) when (
            failure.FailureCode.Equals("NoEligibleModelException", StringComparison.Ordinal))
        {
            return Fail(
                command,
                "model_not_eligible",
                MissingModelAdvice.ForIneligibleRequest(profile),
                69,
                machineReadable);
        }
        catch (BrokerJobFailedException failure)
        {
            // `FailureCode` is an exception type name — PascalCase, and an internal one — so it
            // goes in the message a person reads and never in the code a program switches on.
            return Fail(command, "local_model_failed", failure.Message, 70, machineReadable);
        }
        catch (BrokerBootstrapException bootstrap)
        {
            // These codes are already tokens in this vocabulary — broker_start_timeout,
            // broker_executable_missing — so four distinguishable failures stay four.
            return Fail(command, bootstrap.Code, bootstrap.Message, 75, machineReadable);
        }
        catch (FileNotFoundException missing)
        {
            return Fail(command, "file_missing", missing.Message, 2, machineReadable);
        }
        catch (ArgumentOutOfRangeException outOfRange)
        {
            // A profile the task will not run. The parser catches this before the call,
            // so reaching here means a caller inside this binary passed one — caught
            // ahead of ArgumentException, which it derives from, rather than trusting
            // that the pre-check always keeps it away.
            return Fail(
                command,
                "profile_not_supported",
                outOfRange.Message,
                2,
                machineReadable);
        }
        catch (ArgumentException rejected)
        {
            // Every bound this task enforces — too many files, an image too large, a question
            // that will not fit the context — arrives as this one type carrying a sentence from
            // the catalogue. Splitting them into codes of their own needs typed exceptions in
            // LocalLm.Core, which is a change of its own and not this branch's subject.
            return Fail(command, "input_rejected", rejected.Message, 2, machineReadable);
        }
    }

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
            result.Receipt.Routing?.ResidencyShortfall is ResidencyShortfall.None or null
                ? null
                : result.Receipt.Routing?.VramResidentPercent);

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
