using LocalAi.Broker.Client;
using LocalAi.Contracts;

namespace CodeSearch.Core.Embedding;

public sealed class BrokerEmbeddingClient(
    string model,
    IBrokerClient broker) : IEmbeddingClient
{
    public string Model { get; } =
        !string.IsNullOrWhiteSpace(model)
            ? model
            : throw new ArgumentException("Model cannot be blank.", nameof(model));

    public async Task<float[][]> EmbedAsync(
        IReadOnlyList<string> inputs,
        LocalJobPriority priority,
        string deduplicationKey,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(inputs);
        if (inputs.Count == 0)
        {
            return [];
        }

        var request = LocalJobRequestFactory.CreateEmbed(
            deduplicationKey,
            priority,
            Model,
            inputs);
        LocalJobResult<EmbedJobOutput> result;
        try
        {
            result = await broker.ExecuteAsync<EmbedJobOutput>(
                request,
                cancellationToken);
        }
        catch (BrokerBootstrapException exception) when (exception.Code == "broker_start_timeout")
        {
            throw new EmbeddingUnavailableException(
                "The LocalAi broker did not become available for embedding.",
                exception);
        }
        catch (TimeoutException exception)
        {
            throw new EmbeddingUnavailableException(
                "The LocalAi broker did not become available for embedding.",
                exception);
        }

        if (result.Value.Embeddings.Count != inputs.Count)
        {
            throw new InvalidOperationException(
                $"Broker returned {result.Value.Embeddings.Count} embeddings " +
                $"for {inputs.Count} inputs.");
        }

        return result.Value.Embeddings
            .Select(vector =>
            {
                var values = vector.Select(value => (float)value).ToArray();
                EmbeddingVector.Normalize(values);
                return values;
            })
            .ToArray();
    }
}

public sealed class EmbeddingUnavailableException(
    string message,
    Exception innerException) : Exception(message, innerException);

public static class EmbeddingVector
{
    public static void Normalize(float[] vector)
    {
        ArgumentNullException.ThrowIfNull(vector);
        double sum = 0;
        foreach (var value in vector)
        {
            sum += value * value;
        }

        if (sum <= 0)
        {
            return;
        }

        var scale = (float)(1.0 / Math.Sqrt(sum));
        for (var index = 0; index < vector.Length; index++)
        {
            vector[index] *= scale;
        }
    }
}
