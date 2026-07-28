using System.Security.Cryptography;
using System.Text;
using LocalAi.Broker.Client;
using LocalAi.Contracts;

namespace LocalLm.Core;

public sealed class BrokerLocalModelClient(IBrokerClient broker) : ILocalModelClient
{
    public async Task<LocalJobResult<string>> ChatAsync(
        string model,
        string prompt,
        string? system,
        IReadOnlyList<string>? imagesBase64,
        LocalJobPriority priority,
        CancellationToken cancellationToken = default)
    {
        var request = LocalJobRequestFactory.CreateChat(
            DeduplicationKey(model, prompt, system, imagesBase64),
            priority,
            model,
            prompt,
            system,
            imagesBase64);
        var result = await broker.ExecuteAsync<ChatJobOutput>(
            request,
            cancellationToken);
        return new LocalJobResult<string>(result.Value.Content, result.Receipt);
    }

    public async Task<LocalJobResult<IReadOnlyList<string>>> ListModelsAsync(
        CancellationToken cancellationToken = default)
    {
        var request = LocalJobRequestFactory.CreateListModels(
            "local-lm:list-models",
            LocalJobPriority.Interactive);
        var result = await broker.ExecuteAsync<ListModelsJobOutput>(
            request,
            cancellationToken);
        return new LocalJobResult<IReadOnlyList<string>>(
            result.Value.Models,
            result.Receipt);
    }

    private static string DeduplicationKey(
        string model,
        string prompt,
        string? system,
        IReadOnlyList<string>? imagesBase64)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        Append(hash, model);
        Append(hash, prompt);
        Append(hash, system ?? string.Empty);
        foreach (var image in imagesBase64 ?? [])
        {
            Append(hash, image);
        }

        return "local-lm:chat:" + Convert.ToHexString(hash.GetHashAndReset());
    }

    private static void Append(IncrementalHash hash, string value)
    {
        hash.AppendData(Encoding.UTF8.GetBytes(value));
        hash.AppendData([0]);
    }
}
