using System.Net;
using System.Text;

namespace WorkerRClone.Tests.TestSupport;

/// <summary>
/// An <see cref="HttpMessageHandler"/> that answers every request from a canned
/// response and records what it was asked for.
/// <para>
/// It deliberately never calls <c>base.SendAsync</c> and holds no inner handler,
/// so there is no code path by which a test using it can reach the network.
/// </para>
/// </summary>
public sealed class StubHttpMessageHandler : HttpMessageHandler
{
    private readonly Func<HttpRequestMessage, HttpResponseMessage> _responder;

    /// <summary>All requests seen, in order.</summary>
    public List<HttpRequestMessage> Requests { get; } = new();

    /// <summary>The last request seen, or null when nothing was sent yet.</summary>
    public HttpRequestMessage? LastRequest => Requests.Count == 0 ? null : Requests[^1];

    public StubHttpMessageHandler(Func<HttpRequestMessage, HttpResponseMessage> responder)
    {
        _responder = responder;
    }

    /// <summary>
    /// Always answer with the given status code and (optional) JSON body.
    /// </summary>
    public static StubHttpMessageHandler Returning(HttpStatusCode statusCode, string? jsonBody = null)
    {
        return new StubHttpMessageHandler(_ => new HttpResponseMessage(statusCode)
        {
            Content = new StringContent(jsonBody ?? string.Empty, Encoding.UTF8, "application/json")
        });
    }

    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Requests.Add(request);

        var response = _responder(request);
        response.RequestMessage = request;
        return Task.FromResult(response);
    }
}

/// <summary>
/// An <see cref="IHttpClientFactory"/> that hands out clients backed by a
/// <see cref="StubHttpMessageHandler"/>. Records the client names requested.
/// </summary>
public sealed class StubHttpClientFactory : IHttpClientFactory
{
    private readonly StubHttpMessageHandler _handler;
    private readonly Uri? _baseAddress;

    /// <summary>Names passed to <see cref="CreateClient"/>, in order.</summary>
    public List<string> RequestedClientNames { get; } = new();

    public StubHttpClientFactory(StubHttpMessageHandler handler, Uri? baseAddress = null)
    {
        _handler = handler;
        _baseAddress = baseAddress;
    }

    public HttpClient CreateClient(string name)
    {
        RequestedClientNames.Add(name);
        return new HttpClient(_handler, disposeHandler: false)
        {
            BaseAddress = _baseAddress
        };
    }
}
