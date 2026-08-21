using System.Diagnostics;
using System.Text;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace WorkerGit.Tests.TestSupport;

/**
 * A minimal git smart-HTTP host for Gate D AC7 ("no secret leakage"): real
 * <c>git</c> traffic (<c>info/refs</c>, upload-pack, receive-pack) served by
 * CGI-delegating every request to the real <c>git http-backend</c> binary -
 * so this only has to speak plain CGI, not the smart-HTTP protocol itself -
 * gated by a Basic-auth check against one expected username/token, the same
 * shape a git host (GitHub/Codeberg) presents to a PAT.
 *
 * Modelled after <c>TestSupport.RClone.RCloneStub</c>: an in-process Kestrel
 * host on an ephemeral loopback port, started and torn down per test.
 */
public sealed class SmartHttpGitServer : IAsyncDisposable
{
    private readonly WebApplication _app;
    private readonly string _reposRoot;
    private readonly string _gitPath;
    private readonly string _expectedUsername;
    private readonly string _expectedToken;

    private SmartHttpGitServer(
        WebApplication app, string reposRoot, string gitPath, string expectedUsername, string expectedToken)
    {
        _app = app;
        _reposRoot = reposRoot;
        _gitPath = gitPath;
        _expectedUsername = expectedUsername;
        _expectedToken = expectedToken;
    }

    public static async Task<SmartHttpGitServer> StartAsync(
        string reposRoot,
        string expectedUsername,
        string expectedToken,
        string gitPath = "git",
        CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(reposRoot);

        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Logging.ClearProviders();

        var app = builder.Build();
        var server = new SmartHttpGitServer(app, reposRoot, gitPath, expectedUsername, expectedToken);
        server._map(app);

        await app.StartAsync(cancellationToken);
        return server;
    }

    public string BaseAddress =>
        _app.Urls.FirstOrDefault() ?? throw new InvalidOperationException("The stub is not listening.");

    /** A URL a git client can fetch/push, e.g. <c>RepoUrl("source.git")</c>. */
    public string RepoUrl(string repoRelativePath) => $"{BaseAddress}/{repoRelativePath.TrimStart('/')}";

    public async ValueTask DisposeAsync()
    {
        await _app.StopAsync();
        await _app.DisposeAsync();
    }

    private void _map(WebApplication app)
    {
        app.Run(async context =>
        {
            if (!_isAuthorized(context))
            {
                context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                context.Response.Headers.WWWAuthenticate = "Basic realm=\"WorkerGit.Tests\"";
                await context.Response.WriteAsync("unauthorized");
                return;
            }

            await _runCgiAsync(context);
        });
    }

    private bool _isAuthorized(HttpContext context)
    {
        var header = context.Request.Headers.Authorization.ToString();
        const string prefix = "Basic ";
        if (!header.StartsWith(prefix, StringComparison.Ordinal))
        {
            return false;
        }

        string decoded;
        try
        {
            decoded = Encoding.UTF8.GetString(Convert.FromBase64String(header[prefix.Length..]));
        }
        catch (FormatException)
        {
            return false;
        }

        var separator = decoded.IndexOf(':');
        if (separator < 0)
        {
            return false;
        }

        var username = decoded[..separator];
        var password = decoded[(separator + 1)..];
        return username == _expectedUsername && password == _expectedToken;
    }

    /**
     * CGI-delegates the whole request to <c>git http-backend</c>: the
     * standard mechanism git itself documents for serving repositories over
     * plain HTTP without reimplementing the smart-HTTP wire protocol.
     */
    private async Task _runCgiAsync(HttpContext context)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = _gitPath,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        startInfo.ArgumentList.Add("http-backend");

        var query = context.Request.QueryString.HasValue
            ? context.Request.QueryString.Value!.TrimStart('?')
            : string.Empty;

        var env = startInfo.EnvironmentVariables;
        env["GIT_PROJECT_ROOT"] = _reposRoot;
        env["GIT_HTTP_EXPORT_ALL"] = "1";
        env["PATH_INFO"] = context.Request.Path.Value ?? string.Empty;
        env["QUERY_STRING"] = query;
        env["REQUEST_METHOD"] = context.Request.Method;
        env["CONTENT_TYPE"] = context.Request.ContentType ?? string.Empty;
        env["CONTENT_LENGTH"] = context.Request.ContentLength?.ToString() ?? string.Empty;
        env["GATEWAY_INTERFACE"] = "CGI/1.1";
        env["SERVER_PROTOCOL"] = "HTTP/1.1";
        env["SERVER_SOFTWARE"] = "WorkerGit.Tests-smart-http-stub";
        env["REMOTE_USER"] = _expectedUsername;
        env["GIT_TERMINAL_PROMPT"] = "0";

        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("Failed to start 'git http-backend'.");

        var requestBodyCopy = context.Request.Body.CopyToAsync(process.StandardInput.BaseStream)
            .ContinueWith(_ =>
            {
                try { process.StandardInput.BaseStream.Close(); }
                catch (IOException) { /* backend already closed its side */ }
            });

        var (headers, statusCode) = await _readCgiHeadersAsync(process.StandardOutput.BaseStream);
        context.Response.StatusCode = statusCode;
        foreach (var (name, value) in headers)
        {
            context.Response.Headers[name] = value;
        }

        await process.StandardOutput.BaseStream.CopyToAsync(context.Response.Body);

        await requestBodyCopy;
        await process.WaitForExitAsync();
    }

    /**
     * Reads CGI response headers byte-by-byte off the raw stream (never a
     * <see cref="StreamReader"/> - its internal buffering would swallow
     * leading bytes of the binary packfile body that follows the blank
     * line), stopping exactly at the header/body boundary.
     */
    private static async Task<(List<(string Name, string Value)> Headers, int StatusCode)> _readCgiHeadersAsync(
        Stream stdout)
    {
        var headers = new List<(string, string)>();
        var statusCode = StatusCodes.Status200OK;

        while (true)
        {
            var line = await _readLineAsync(stdout);
            if (line is null || line.Length == 0)
            {
                break;
            }

            var colon = line.IndexOf(':');
            if (colon < 0)
            {
                continue;
            }

            var name = line[..colon].Trim();
            var value = line[(colon + 1)..].Trim();

            if (string.Equals(name, "Status", StringComparison.OrdinalIgnoreCase))
            {
                var codeToken = value.Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
                if (codeToken is not null && int.TryParse(codeToken, out var parsed))
                {
                    statusCode = parsed;
                }
            }
            else
            {
                headers.Add((name, value));
            }
        }

        return (headers, statusCode);
    }

    private static async Task<string?> _readLineAsync(Stream stream)
    {
        var buffer = new List<byte>();
        var singleByte = new byte[1];
        var sawAnyByte = false;

        while (true)
        {
            var read = await stream.ReadAsync(singleByte.AsMemory(0, 1));
            if (read == 0)
            {
                return sawAnyByte ? Encoding.ASCII.GetString(buffer.ToArray()) : null;
            }

            sawAnyByte = true;
            if (singleByte[0] == (byte)'\n')
            {
                break;
            }

            buffer.Add(singleByte[0]);
        }

        if (buffer.Count > 0 && buffer[^1] == (byte)'\r')
        {
            buffer.RemoveAt(buffer.Count - 1);
        }

        return Encoding.ASCII.GetString(buffer.ToArray());
    }
}
