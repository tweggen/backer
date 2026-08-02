using System.Net;

namespace Tools.Tests;

/// <summary>
/// A captured snapshot of a request as the inner (transport) handler saw it.
/// The body is read at capture time, exactly like a real transport does, so a
/// handler that hands the same already-consumed content to a second send is
/// caught here rather than silently passing.
/// </summary>
public sealed record CapturedRequest(
    HttpMethod Method,
    Uri? RequestUri,
    string? AuthorizationScheme,
    string? AuthorizationParameter,
    string? ContentType,
    byte[]? Body)
{
    public string? BodyAsString => Body is null ? null : System.Text.Encoding.UTF8.GetString(Body);
}

/// <summary>
/// Fake inner handler. Records every request it is asked to send (including the
/// body bytes, streamed out via <see cref="HttpContent.CopyToAsync(Stream, CancellationToken)"/>
/// the way a real transport does) and replies with scripted responses in order.
/// </summary>
public sealed class RecordingHandler : HttpMessageHandler
{
    private readonly Queue<Func<HttpRequestMessage, HttpResponseMessage>> _responses = new();

    public List<CapturedRequest> Requests { get; } = new();

    public int SendCount => Requests.Count;

    public RecordingHandler Respond(HttpStatusCode statusCode, string? content = null)
    {
        _responses.Enqueue(_ => new HttpResponseMessage(statusCode)
        {
            Content = content is null ? new StringContent(string.Empty) : new StringContent(content)
        });
        return this;
    }

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        byte[]? body = null;
        string? contentType = null;
        if (request.Content is not null)
        {
            contentType = request.Content.Headers.ContentType?.ToString();
            using var buffer = new MemoryStream();
            // A real transport serialises the content to the wire exactly once
            // per send. Re-using an already-consumed, non-rewindable content
            // therefore throws right here.
            await request.Content.CopyToAsync(buffer, cancellationToken);
            body = buffer.ToArray();
        }

        Requests.Add(new CapturedRequest(
            request.Method,
            request.RequestUri,
            request.Headers.Authorization?.Scheme,
            request.Headers.Authorization?.Parameter,
            contentType,
            body));

        if (_responses.Count == 0)
        {
            throw new InvalidOperationException(
                $"RecordingHandler received an unexpected request #{Requests.Count} " +
                $"({request.Method} {request.RequestUri}); no scripted response left.");
        }

        var response = _responses.Dequeue()(request);
        response.RequestMessage = request;
        return response;
    }
}

/// <summary>
/// Minimal stateful <see cref="IStaticTokenProvider"/> stub.
/// </summary>
public sealed class FakeStaticTokenProvider : IStaticTokenProvider
{
    private string? _token;

    public FakeStaticTokenProvider(string? initialToken = null) => _token = initialToken;

    public List<string> SetTokenCalls { get; } = new();

    public Task<string?> GetToken() => Task.FromResult(_token);

    public void SetToken(string token)
    {
        SetTokenCalls.Add(token);
        _token = token;
    }
}

/// <summary>
/// A forward-only, non-seekable stream: content built on top of it can be read
/// exactly once, which is what makes the rebuffering bug observable.
/// </summary>
public sealed class ForwardOnlyStream : Stream
{
    private readonly MemoryStream _inner;

    public ForwardOnlyStream(byte[] data) => _inner = new MemoryStream(data);

    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException();

    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    public override void Flush() { }
    public override int Read(byte[] buffer, int offset, int count) => _inner.Read(buffer, offset, count);
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (disposing) _inner.Dispose();
        base.Dispose(disposing);
    }
}
