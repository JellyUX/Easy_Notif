using System.Net;

namespace Jellyfin.Plugin.EasyNotif.Tests.TestDoubles;

/// <summary>
/// A test <see cref="HttpMessageHandler"/> that returns queued responses and records every request
/// it received (method, URI, headers, body). Also exposes an <see cref="IHttpClientFactory"/> that
/// hands out a client bound to this handler.
/// </summary>
public sealed class StubHttpMessageHandler : HttpMessageHandler, IHttpClientFactory
{
    private readonly Queue<Func<HttpRequestMessage, HttpResponseMessage>> _responders = new();

    /// <summary>Gets the requests this handler received, in order.</summary>
    public List<RecordedRequest> Requests { get; } = [];

    /// <summary>Queues a plain JSON response.</summary>
    /// <param name="statusCode">The status code.</param>
    /// <param name="json">The response body.</param>
    /// <param name="retryAfterSeconds">An optional <c>Retry-After</c> header value in seconds.</param>
    /// <returns>This handler, for chaining.</returns>
    public StubHttpMessageHandler EnqueueJson(HttpStatusCode statusCode, string json, int? retryAfterSeconds = null)
    {
        _responders.Enqueue(_ =>
        {
            var response = new HttpResponseMessage(statusCode)
            {
                Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json")
            };
            if (retryAfterSeconds is { } seconds)
            {
                response.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.FromSeconds(seconds));
            }

            return response;
        });
        return this;
    }

    /// <summary>Queues a thrown <see cref="HttpRequestException"/> for the next request.</summary>
    /// <returns>This handler, for chaining.</returns>
    public StubHttpMessageHandler EnqueueNetworkFailure()
    {
        _responders.Enqueue(_ => throw new HttpRequestException("simulated network failure"));
        return this;
    }

    /// <inheritdoc/>
    public HttpClient CreateClient(string name) => new(this, disposeHandler: false);

    /// <inheritdoc/>
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var body = request.Content is null
            ? null
            : await request.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

        Requests.Add(new RecordedRequest(
            request.Method,
            request.RequestUri,
            request.Headers.Authorization?.ToString(),
            request.Headers.TryGetValues("Idempotency-Key", out var keys) ? string.Join(",", keys) : null,
            body));

        if (_responders.Count == 0)
        {
            throw new InvalidOperationException("StubHttpMessageHandler received an unexpected request.");
        }

        return _responders.Dequeue()(request);
    }

    /// <summary>One request the handler observed.</summary>
    /// <param name="Method">The HTTP method.</param>
    /// <param name="Uri">The request URI.</param>
    /// <param name="Authorization">The Authorization header value, or null.</param>
    /// <param name="IdempotencyKey">The Idempotency-Key header value, or null.</param>
    /// <param name="Body">The request body, or null.</param>
    public sealed record RecordedRequest(HttpMethod Method, Uri? Uri, string? Authorization, string? IdempotencyKey, string? Body);
}
