using System.Text;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace TestSupport.RClone;

/// <summary>
/// An in-process stand-in for rclone's remote-control API.
///
/// rclone's RC interface is plain HTTP + JSON, so the agent cannot tell this
/// apart from the real thing: no process is started, no byte is transferred,
/// and the test decides whether a job succeeds, fails, or hangs.
///
/// Two properties make it useful beyond "not crashing":
/// <list type="bullet">
/// <item>every request is recorded with its body, so a test can assert on the
/// remote configuration written and the <c>remote:/path</c> URIs the agent
/// built - the actual contract between Backer and rclone;</item>
/// <item><see cref="WaitForRequestAsync"/> turns "the agent got there" into an
/// awaitable condition, so tests never sleep.</item>
/// </list>
///
/// <para><b>Fidelity caveat.</b> The payloads below are shaped to what
/// <c>WorkerRClone.Client.RCloneClient</c> reads. That makes the stub a valid
/// double for these tests, but it does not by itself prove the field names
/// match a real rclone. Confirming that needs an opt-in test against a real
/// binary, in the style of the existing live OneDrive check. Until then, treat
/// agreement with rclone as asserted, not verified - and note that
/// <c>JobListResult</c> reads <c>running_ids</c> / <c>jobsids</c>, spellings
/// this stub reproduces without having checked them against rclone.</para>
/// </summary>
public sealed class RCloneStub : IAsyncDisposable
{
    /// <summary>
    /// The credentials <c>RCloneService</c> hardcodes when talking to rclone
    /// (<c>worker/WorkerRClone/Services/RCloneService.cs:1036-1039</c>).
    /// </summary>
    public const string DefaultUsername = "who";

    public const string DefaultPassword = "how";

    private static readonly TimeSpan DefaultWaitTimeout = TimeSpan.FromSeconds(10);

    private readonly WebApplication _app;
    private readonly string _expectedAuthHeader;

    private readonly object _lock = new();
    private readonly List<RecordedRequest> _requests = new();
    private readonly List<Waiter> _waiters = new();
    private readonly List<string> _remotes = new();
    private readonly Dictionary<string, JsonObject> _remoteParameters = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<int, StubJob> _jobs = new();

    private int _nextJobId;
    private bool _quitRequested;

    private RCloneStub(WebApplication app, string username, string password)
    {
        _app = app;
        _expectedAuthHeader =
            "Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes($"{username}:{password}"));
    }

    /// <summary>
    /// Starts a stub on an ephemeral loopback port. Two stubs can run at once.
    /// </summary>
    public static async Task<RCloneStub> StartAsync(
        string username = DefaultUsername,
        string password = DefaultPassword,
        CancellationToken cancellationToken = default)
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Logging.ClearProviders();

        var app = builder.Build();
        var stub = new RCloneStub(app, username, password);
        stub.MapEndpoints(app);

        await app.StartAsync(cancellationToken);
        return stub;
    }

    /// <summary>Base address of the running stub, e.g. <c>http://127.0.0.1:53412</c>.</summary>
    public string BaseAddress =>
        _app.Urls.FirstOrDefault()
        ?? throw new InvalidOperationException("The stub is not listening on any address.");

    /// <summary>
    /// An <see cref="HttpClient"/> pointed at the stub and carrying the same
    /// basic auth header the agent sends.
    /// </summary>
    public HttpClient CreateClient()
    {
        var client = new HttpClient { BaseAddress = new Uri(BaseAddress) };
        client.DefaultRequestHeaders.TryAddWithoutValidation("Authorization", _expectedAuthHeader);
        return client;
    }

    /// <summary>Scripted behaviour for jobs the stub is asked to start.</summary>
    public RCloneStubScript Script { get; } = new();

    /// <summary>The <c>core/stats</c> payload returned to every caller.</summary>
    public StubStats Stats { get; } = new();

    /// <summary>True once <c>core/quit</c> has been called.</summary>
    public bool QuitRequested
    {
        get { lock (_lock) { return _quitRequested; } }
    }

    /// <summary>Every request received, oldest first.</summary>
    public IReadOnlyList<RecordedRequest> Requests
    {
        get { lock (_lock) { return _requests.ToArray(); } }
    }

    /// <summary>Remote names created through <c>config/create</c>, in order.</summary>
    public IReadOnlyList<string> Remotes
    {
        get { lock (_lock) { return _remotes.ToArray(); } }
    }

    /// <summary>
    /// The parameters a remote was created with, or null if it was never
    /// created. This is what ends up in <c>rclone.conf</c> in production.
    /// </summary>
    public JsonObject? GetRemoteParameters(string remoteName)
    {
        lock (_lock)
        {
            return _remoteParameters.TryGetValue(remoteName, out var parameters)
                ? (JsonObject)parameters.DeepClone()
                : null;
        }
    }

    /// <summary>All requests recorded for one RC path, oldest first.</summary>
    public IReadOnlyList<RecordedRequest> RequestsFor(string path)
    {
        lock (_lock)
        {
            return _requests.Where(r => PathMatches(r.Path, path)).ToArray();
        }
    }

    /// <summary>
    /// Completes once the stub has received at least <paramref name="count"/>
    /// requests for <paramref name="path"/>, returning the
    /// <paramref name="count"/>-th. Requests already received count, so this is
    /// free of races with the agent.
    ///
    /// The timeout is a ceiling on a condition, not a delay: the normal case
    /// returns as soon as the request arrives.
    /// </summary>
    public async Task<RecordedRequest> WaitForRequestAsync(
        string path,
        int count = 1,
        TimeSpan? timeout = null,
        CancellationToken cancellationToken = default)
    {
        if (count < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(count), count, "count must be at least 1.");
        }

        Waiter waiter;
        lock (_lock)
        {
            var matching = _requests.Where(r => PathMatches(r.Path, path)).ToList();
            if (matching.Count >= count)
            {
                return matching[count - 1];
            }

            waiter = new Waiter(path, count);
            _waiters.Add(waiter);
        }

        using var timeoutSource = new CancellationTokenSource(timeout ?? DefaultWaitTimeout);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(
            timeoutSource.Token, cancellationToken);

        try
        {
            return await waiter.Completion.Task.WaitAsync(linked.Token);
        }
        catch (OperationCanceledException) when (timeoutSource.IsCancellationRequested)
        {
            lock (_lock)
            {
                _waiters.Remove(waiter);
                var seen = _requests.Count == 0
                    ? "(none)"
                    : string.Join(", ", _requests.Select(r => r.Path).Distinct());
                throw new TimeoutException(
                    $"Timed out waiting for request #{count} to '{path}'. Paths seen so far: {seen}.");
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _app.StopAsync();
        await _app.DisposeAsync();
    }

    // ------------------------------------------------------------------
    // Endpoints - the subset of the RC API that RCloneClient actually calls
    // ------------------------------------------------------------------

    private void MapEndpoints(WebApplication app)
    {
        app.Use(async (context, next) =>
        {
            if (context.Request.Headers.Authorization != _expectedAuthHeader)
            {
                context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                await context.Response.WriteAsync("unauthorized");
                return;
            }

            await next(context);
        });

        Map(app, "/config/listremotes", _ =>
        {
            lock (_lock)
            {
                var remotes = new JsonArray();
                foreach (var remote in _remotes)
                {
                    remotes.Add(JsonValue.Create(remote));
                }

                return new JsonObject { ["remotes"] = remotes };
            }
        });

        Map(app, "/config/create", request =>
        {
            var name = request.GetString("name");
            if (string.IsNullOrEmpty(name))
            {
                throw new StubRequestException("config/create: 'name' is required");
            }

            lock (_lock)
            {
                if (!_remotes.Contains(name))
                {
                    _remotes.Add(name);
                }

                _remoteParameters[name] =
                    request.Json.TryGetProperty("parameters", out var parameters)
                    && JsonNode.Parse(parameters.GetRawText()) is JsonObject parsed
                        ? parsed
                        : new JsonObject();
            }

            return new JsonObject();
        });

        Map(app, "/config/paths", _ => new JsonObject
        {
            ["config"] = "/stub/rclone.conf",
            ["cache"] = "/stub/cache",
            ["temp"] = "/stub/temp"
        });

        Map(app, "/sync/copy", request => StartJob("sync/copy", request));
        Map(app, "/sync/sync", request => StartJob("sync/sync", request));
        Map(app, "/rc/noop", request => StartJob("rc/noop", request));

        Map(app, "/job/status", request =>
        {
            var jobId = GetJobId(request);

            lock (_lock)
            {
                if (!_jobs.TryGetValue(jobId, out var job))
                {
                    throw new StubRequestException($"job not found: {jobId}");
                }

                job.Poll();
                return job.ToStatusJson();
            }
        });

        Map(app, "/job/stop", request =>
        {
            var jobId = GetJobId(request);

            lock (_lock)
            {
                if (!_jobs.TryGetValue(jobId, out var job))
                {
                    throw new StubRequestException($"job not found: {jobId}");
                }

                job.Stop();
                return new JsonObject();
            }
        });

        Map(app, "/job/list", _ =>
        {
            lock (_lock)
            {
                var all = new JsonArray();
                var running = new JsonArray();
                var finished = new JsonArray();

                foreach (var job in _jobs.Values.OrderBy(j => j.Id))
                {
                    all.Add(JsonValue.Create(job.Id));
                    (job.Finished ? finished : running).Add(JsonValue.Create(job.Id));
                }

                /*
                 * These four names are the ones JobListResult declares. Only
                 * one spelling is emitted on purpose: sending both 'jobids'
                 * and 'jobsids' would paper over the open question of which
                 * one real rclone uses instead of leaving it visible.
                 */
                return new JsonObject
                {
                    ["executeId"] = "stub-execute-id",
                    ["jobsids"] = all,
                    ["running_ids"] = running,
                    ["finished_ids"] = finished
                };
            }
        });

        Map(app, "/core/stats", _ => Stats.ToJson());

        Map(app, "/core/quit", _ =>
        {
            lock (_lock)
            {
                _quitRequested = true;
            }

            return new JsonObject();
        });
    }

    private JsonObject StartJob(string kind, RecordedRequest request)
    {
        lock (_lock)
        {
            var id = ++_nextJobId;
            _jobs[id] = new StubJob(id, Script.TakeNext());

            return new JsonObject { ["jobid"] = id };
        }
    }

    private static int GetJobId(RecordedRequest request)
    {
        if (request.Json.ValueKind == System.Text.Json.JsonValueKind.Object
            && request.Json.TryGetProperty("jobid", out var value)
            && value.TryGetInt32(out var jobId))
        {
            return jobId;
        }

        throw new StubRequestException($"{request.Path}: 'jobid' is required");
    }

    /// <summary>
    /// Wires one RC path: record the request, run the handler, and translate a
    /// <see cref="StubRequestException"/> into the error envelope rclone
    /// returns (which <c>RCloneClient</c> surfaces as a thrown exception).
    /// </summary>
    private void Map(WebApplication app, string path, Func<RecordedRequest, JsonObject> handler)
    {
        app.MapPost(path, async context =>
        {
            using var reader = new StreamReader(context.Request.Body, Encoding.UTF8);
            var body = await reader.ReadToEndAsync();

            var recorded = Record(path, body);

            JsonObject response;
            try
            {
                response = handler(recorded);
            }
            catch (StubRequestException e)
            {
                context.Response.StatusCode = StatusCodes.Status500InternalServerError;
                context.Response.ContentType = "application/json";
                await context.Response.WriteAsync(new JsonObject
                {
                    ["error"] = e.Message,
                    ["input"] = new JsonObject(),
                    ["path"] = path.TrimStart('/'),
                    ["status"] = 500
                }.ToJsonString());
                return;
            }

            context.Response.ContentType = "application/json";
            await context.Response.WriteAsync(response.ToJsonString());
        });
    }

    private RecordedRequest Record(string path, string body)
    {
        var recorded = new RecordedRequest(path, body, DateTimeOffset.UtcNow);

        List<Waiter> satisfied = new();
        lock (_lock)
        {
            _requests.Add(recorded);

            for (var i = _waiters.Count - 1; i >= 0; i--)
            {
                var waiter = _waiters[i];
                var matching = _requests.Where(r => PathMatches(r.Path, waiter.Path)).ToList();
                if (matching.Count >= waiter.Count)
                {
                    waiter.Match = matching[waiter.Count - 1];
                    satisfied.Add(waiter);
                    _waiters.RemoveAt(i);
                }
            }
        }

        /*
         * Completed outside the lock: a continuation running inline must not be
         * able to re-enter the stub while it is held.
         */
        foreach (var waiter in satisfied)
        {
            waiter.Completion.TrySetResult(waiter.Match!);
        }

        return recorded;
    }

    private static bool PathMatches(string recordedPath, string requestedPath) =>
        string.Equals(
            recordedPath.TrimStart('/'),
            requestedPath.TrimStart('/'),
            StringComparison.OrdinalIgnoreCase);

    private sealed class Waiter
    {
        public Waiter(string path, int count)
        {
            Path = path;
            Count = count;
        }

        public string Path { get; }
        public int Count { get; }
        public RecordedRequest? Match { get; set; }

        public TaskCompletionSource<RecordedRequest> Completion { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private sealed class StubRequestException : Exception
    {
        public StubRequestException(string message) : base(message)
        {
        }
    }

    private sealed class StubJob
    {
        private readonly StubJobBehaviour _behaviour;
        private readonly DateTimeOffset _startedAt = DateTimeOffset.UtcNow;
        private int _polls;
        private bool _stopped;

        public StubJob(int id, StubJobBehaviour behaviour)
        {
            Id = id;
            _behaviour = behaviour;
        }

        public int Id { get; }

        public bool Finished { get; private set; }

        public void Poll()
        {
            if (Finished)
            {
                return;
            }

            if (_behaviour.NeverFinish)
            {
                _polls++;
                return;
            }

            if (_polls >= _behaviour.CompleteAfterPolls)
            {
                Finished = true;
            }

            _polls++;
        }

        public void Stop()
        {
            Finished = true;
            _stopped = true;
        }

        public JsonObject ToStatusJson()
        {
            var failed = _stopped || _behaviour.Error is not null;
            var error = _stopped ? "context canceled" : _behaviour.Error ?? string.Empty;

            return new JsonObject
            {
                ["id"] = Id,
                ["finished"] = Finished,
                ["success"] = Finished && !failed,
                ["error"] = Finished ? error : string.Empty,
                ["duration"] = (DateTimeOffset.UtcNow - _startedAt).TotalSeconds,
                ["startTime"] = _startedAt.ToString("o"),
                ["endTime"] = Finished ? DateTimeOffset.UtcNow.ToString("o") : string.Empty,
                ["group"] = $"job/{Id}"
            };
        }
    }
}
