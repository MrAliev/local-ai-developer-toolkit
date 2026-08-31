using CodeSearch.Core.Chunking;
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
            inputs,
            requestedContextTokens: ChunkLimits.EmbeddingContextTokens);
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
                // The transport rejects non-finite doubles, but a finite double past
                // float.MaxValue casts to Infinity, the sum of squares follows, the scale
                // becomes zero and the product NaN — durably persisted into the index and
                // poisoning every ranking comparison it touches (#205). Checked here, at the
                // conversion, so the invariant does not hang on one transport implementation.
                var values = new float[vector.Count];
                for (var index = 0; index < values.Length; index++)
                {
                    var value = (float)vector[index];
                    if (!float.IsFinite(value))
                    {
                        throw new InvalidDataException(
                            $"The embedding backend returned {vector[index]} at position " +
                            $"{index}, which is not representable as a finite float.");
                    }

                    values[index] = value;
                }

                EmbeddingVector.Normalize(values);
                return values;
            })
            .ToArray();
    }
}

public sealed class EmbeddingUnavailableException(
    string message,
    Exception innerException) : Exception(message, innerException);

/// <summary>
/// A single chunk the embedding model refused, already isolated from its batch. Carries the file
/// and chunk so the operator can act on it; the broker's own job id names nothing a human owns.
/// </summary>
public sealed class EmbeddingChunkException(
    string message,
    Exception innerException) : Exception(message, innerException);

public static class EmbeddingVector
{
    /// <summary>
    /// Normalization refuses what it cannot normalize (#205): a zero vector used to pass
    /// through silently and a non-finite sum produced NaNs that survived into the durable
    /// index. Both are backend anomalies worth one loud failure, not a poisoned ranking.
    /// </summary>
    public static void Normalize(float[] vector)
    {
        ArgumentNullException.ThrowIfNull(vector);
        double sum = 0;
        foreach (var value in vector)
        {
            sum += value * value;
        }

        if (!double.IsFinite(sum) || sum <= 0)
        {
            throw new InvalidDataException(
                sum <= 0
                    ? "The embedding backend returned a zero vector, which cannot be normalized."
                    : "The embedding vector's magnitude is not finite.");
        }

        var scale = (float)(1.0 / Math.Sqrt(sum));
        for (var index = 0; index < vector.Length; index++)
        {
            vector[index] *= scale;
            if (!float.IsFinite(vector[index]))
            {
                throw new InvalidDataException(
                    "Normalizing the embedding produced a non-finite value.");
            }
        }
    }
}
