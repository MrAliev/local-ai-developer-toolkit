using System.Globalization;
using System.Text.Json;
using LocalAi.Broker.Client;
using LocalAi.Contracts;
using LocalLm.Core;

namespace LocalAi.Cli;

public static class ModelCommand
{
    public const int SuccessExitCode = 0;
    public const int FailureExitCode = 1;
    public const int InvalidArgumentsExitCode = 2;
    public const int RejectedExitCode = 3;
    public const int CancelledExitCode = 4;

    private const int SchemaVersion = 1;
    private const int MaximumStatusModels = 256;

    public static async Task<int> ExecuteAsync(
        IReadOnlyList<string> arguments,
        ILocalModelClient client,
        TextWriter output,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(output);

        if (!TryParse(arguments, out var request))
        {
            await WriteAsync(
                output,
                new ModelCommandError(
                    SchemaVersion, "invalid", false, "invalid_arguments"));
            return InvalidArgumentsExitCode;
        }

        try
        {
            return request.Operation switch
            {
                "status" => await StatusAsync(client, output, cancellationToken),
                "pull" => await PullAsync(request, client, output, cancellationToken),
                "preflight" => await PreflightAsync(request, client, output, cancellationToken),
                _ => throw new InvalidOperationException(),
            };
        }
        catch (BrokerJobFailedException exception) when (
            request.Operation == "preflight" &&
            string.Equals(
                exception.FailureCode,
                "ModelPreflightException",
                StringComparison.Ordinal))
        {
            await WriteAsync(
                output,
                new ModelPreflightCommandRejected(
                    SchemaVersion,
                    "preflight",
                    false,
                    request.Model!,
                    request.ContextTokens,
                    "residency_rejected"));
            return RejectedExitCode;
        }
        catch (OperationCanceledException)
        {
            await WriteAsync(
                output,
                new ModelCommandError(
                    SchemaVersion, request.Operation, false, "cancelled"));
            return CancelledExitCode;
        }
        catch (Exception)
        {
            await WriteAsync(
                output,
                new ModelCommandError(
                    SchemaVersion, request.Operation, false, "broker_failure"));
            return FailureExitCode;
        }
    }

    private static async Task<int> StatusAsync(
        ILocalModelClient client,
        TextWriter output,
        CancellationToken cancellationToken)
    {
        var result = await client.GetModelsStatusAsync(cancellationToken);
        var status = result.Value ?? throw new InvalidOperationException();
        var installed = ValidateAndSortModels(status.InstalledModels);
        var pending = ValidateAndSortModels(status.PendingPullModels ?? []);
        if (!IsSafeCatalogVersion(status.CatalogVersion))
        {
            throw new InvalidOperationException();
        }

        await WriteAsync(
            output,
            new ModelStatusCommandSuccess(
                SchemaVersion,
                "status",
                true,
                status.CatalogVersion,
                installed,
                pending));
        return SuccessExitCode;
    }

    private static async Task<int> PullAsync(
        Request request,
        ILocalModelClient client,
        TextWriter output,
        CancellationToken cancellationToken)
    {
        var result = await client.PullModelAsync(
            request.Model!,
            request.CatalogVersion!,
            cancellationToken);
        if (!string.Equals(result.Value?.Status, "success", StringComparison.Ordinal))
        {
            throw new InvalidOperationException();
        }

        await WriteAsync(
            output,
            new ModelPullCommandSuccess(
                SchemaVersion,
                "pull",
                true,
                request.Model!,
                request.CatalogVersion!,
                "success"));
        return SuccessExitCode;
    }

    private static async Task<int> PreflightAsync(
        Request request,
        ILocalModelClient client,
        TextWriter output,
        CancellationToken cancellationToken)
    {
        var result = await client.PreflightModelAsync(
            request.Model!,
            request.ContextTokens,
            cancellationToken);
        var proof = result.Value ?? throw new InvalidOperationException();
        if (!IsSafeModel(proof.Model))
        {
            throw new InvalidOperationException();
        }

        await WriteAsync(
            output,
            new ModelPreflightCommandSuccess(
                SchemaVersion,
                "preflight",
                true,
                proof.Model,
                proof.ContextTokens,
                proof.SizeBytes,
                proof.SizeVramBytes,
                proof.FullyResident,
                proof.VerifiedAtUtc));
        return SuccessExitCode;
    }

    private static string[] ValidateAndSortModels(IReadOnlyList<string>? models)
    {
        if (models is null || models.Count > MaximumStatusModels ||
            models.Any(model => !IsSafeModel(model)) ||
            models.Distinct(StringComparer.Ordinal).Count() != models.Count)
        {
            throw new InvalidOperationException();
        }

        return models.Order(StringComparer.Ordinal).ToArray();
    }

    private static bool TryParse(
        IReadOnlyList<string> arguments,
        out Request request)
    {
        request = new Request("invalid", null, null, 0);
        if (arguments.Count == 1 &&
            string.Equals(arguments[0], "status", StringComparison.Ordinal))
        {
            request = new Request("status", null, null, 0);
            return true;
        }

        if (arguments.Count == 5 &&
            string.Equals(arguments[0], "pull", StringComparison.Ordinal) &&
            string.Equals(arguments[1], "--model", StringComparison.Ordinal) &&
            string.Equals(arguments[3], "--catalog-version", StringComparison.Ordinal) &&
            IsSafeModel(arguments[2]) &&
            IsSafeCatalogVersion(arguments[4]))
        {
            request = new Request("pull", arguments[2], arguments[4], 0);
            return true;
        }

        if (arguments.Count == 5 &&
            string.Equals(arguments[0], "preflight", StringComparison.Ordinal) &&
            string.Equals(arguments[1], "--model", StringComparison.Ordinal) &&
            string.Equals(arguments[3], "--context", StringComparison.Ordinal) &&
            IsSafeModel(arguments[2]) &&
            int.TryParse(
                arguments[4],
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out var contextTokens) &&
            LocalContextTiers.IsSupported(contextTokens))
        {
            request = new Request("preflight", arguments[2], null, contextTokens);
            return true;
        }

        return false;
    }

    internal static bool IsSafeModel(string? value)
    {
        if (string.IsNullOrEmpty(value) || value.Length > 200 ||
            !IsAsciiAlphaNumeric(value[0]) ||
            !IsAsciiAlphaNumeric(value[^1]))
        {
            return false;
        }

        return value.All(character =>
                IsAsciiAlphaNumeric(character) || character is '.' or '_' or '-' or '/' or ':') &&
            !value.Contains("..", StringComparison.Ordinal) &&
            !value.Contains("//", StringComparison.Ordinal) &&
            !value.Contains("::", StringComparison.Ordinal) &&
            !value.Contains("/:", StringComparison.Ordinal) &&
            !value.Contains(":/", StringComparison.Ordinal);
    }

    internal static bool IsSafeCatalogVersion(string? value)
    {
        if (string.IsNullOrEmpty(value) || value.Length > 128 ||
            !IsAsciiAlphaNumeric(value[0]) ||
            !IsAsciiAlphaNumeric(value[^1]))
        {
            return false;
        }

        return value.All(character =>
            IsAsciiAlphaNumeric(character) || character is '.' or '_' or '-');
    }

    private static bool IsAsciiAlphaNumeric(char value) =>
        value is >= 'a' and <= 'z' or >= 'A' and <= 'Z' or >= '0' and <= '9';

    private static Task WriteAsync<T>(TextWriter output, T response) =>
        output.WriteLineAsync(JsonSerializer.Serialize(response, LocalAiJson.Strict));

    private sealed record Request(
        string Operation,
        string? Model,
        string? CatalogVersion,
        int ContextTokens);
}
