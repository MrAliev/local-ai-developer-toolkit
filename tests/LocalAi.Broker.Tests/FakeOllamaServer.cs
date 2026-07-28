using System.Collections.Concurrent;
using System.Net;
using System.Text;

namespace LocalAi.Broker.Tests;

internal sealed class FakeOllamaServer : HttpMessageHandler
{
    private readonly ConcurrentQueue<Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>>> _responses = new();

    public List<CapturedRequest> Requests { get; } = [];

    public bool IsDisposed { get; private set; }

    public void EnqueueJson(HttpStatusCode statusCode, string json, Action<HttpResponseMessage>? configure = null)
    {
        _responses.Enqueue((_, _) =>
        {
            var response = new HttpResponseMessage(statusCode)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json")
            };
            configure?.Invoke(response);
            return Task.FromResult(response);
        });
    }

    public void EnqueueException(Exception exception) =>
        _responses.Enqueue((_, _) => Task.FromException<HttpResponseMessage>(exception));

    public void Enqueue(
        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> response) =>
        _responses.Enqueue(response);

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        var body = request.Content is null
            ? null
            : await request.Content.ReadAsStringAsync(cancellationToken);
        lock (Requests)
        {
            Requests.Add(new CapturedRequest(
                request.Method,
                request.RequestUri ?? throw new InvalidOperationException("Request URI is required."),
                body));
        }

        if (!_responses.TryDequeue(out var response))
        {
            throw new InvalidOperationException("No fake Ollama response was configured.");
        }

        return await response(request, cancellationToken);
    }

    protected override void Dispose(bool disposing)
    {
        IsDisposed = true;
        base.Dispose(disposing);
    }
}

internal sealed record CapturedRequest(HttpMethod Method, Uri Uri, string? Body);
