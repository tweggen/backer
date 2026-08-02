using System.Collections.Concurrent;
using Microsoft.AspNetCore.SignalR;

namespace Hannibal.IntegrationTests.TestSupport;

/// <summary>
/// One recorded SignalR broadcast: which audience it went to, which client
/// method was invoked and with which arguments.
/// </summary>
public sealed record HubBroadcast(string Target, string Method, IReadOnlyList<object?> Args)
{
    public override string ToString()
        => $"{Target}.{Method}({string.Join(", ", Args.Select(a => a?.ToString() ?? "<null>"))})";
}


/// <summary>
/// An <see cref="IClientProxy"/> that records instead of transmitting.
///
/// <c>Clients.All.SendAsync("X", arg)</c> is an extension method that funnels
/// into <see cref="SendCoreAsync"/>, so recording at this single method captures
/// every overload of SendAsync.
/// </summary>
public sealed class RecordingClientProxy : IClientProxy
{
    private readonly string _target;
    private readonly ConcurrentQueue<HubBroadcast> _sink;

    public RecordingClientProxy(string target, ConcurrentQueue<HubBroadcast> sink)
    {
        _target = target;
        _sink = sink;
    }

    public Task SendCoreAsync(
        string method,
        object?[] args,
        CancellationToken cancellationToken = default)
    {
        _sink.Enqueue(new HubBroadcast(_target, method, args ?? Array.Empty<object?>()));
        return Task.CompletedTask;
    }
}


/// <summary>
/// A fully recording <see cref="IHubContext{THub}"/>. Substituted for the real
/// one in the test host so that hub broadcasts are assertable without a live
/// SignalR connection - and so that nothing tries to serialise to a transport
/// that does not exist under <c>TestServer</c>.
/// </summary>
public sealed class RecordingHubContext<THub> : IHubContext<THub>
    where THub : Hub
{
    private readonly ConcurrentQueue<HubBroadcast> _broadcasts = new();

    public IHubClients Clients { get; }

    public IGroupManager Groups { get; } = new RecordingGroupManager();

    public RecordingHubContext()
    {
        Clients = new RecordingHubClients(_broadcasts);
    }

    /// <summary>Everything sent through this hub context, oldest first.</summary>
    public IReadOnlyList<HubBroadcast> Broadcasts => _broadcasts.ToArray();

    /// <summary>Broadcasts that invoked the named client method.</summary>
    public IReadOnlyList<HubBroadcast> BroadcastsOf(string method)
        => _broadcasts.Where(b => b.Method == method).ToArray();

    public void Clear()
    {
        while (_broadcasts.TryDequeue(out _))
        {
        }
    }


    private sealed class RecordingHubClients : IHubClients
    {
        private readonly ConcurrentQueue<HubBroadcast> _sink;

        public RecordingHubClients(ConcurrentQueue<HubBroadcast> sink)
        {
            _sink = sink;
            All = new RecordingClientProxy("All", sink);
        }

        public IClientProxy All { get; }

        public IClientProxy AllExcept(IReadOnlyList<string> excludedConnectionIds)
            => _proxy($"AllExcept({string.Join(",", excludedConnectionIds)})");

        public IClientProxy Client(string connectionId)
            => _proxy($"Client({connectionId})");

        public IClientProxy Clients(IReadOnlyList<string> connectionIds)
            => _proxy($"Clients({string.Join(",", connectionIds)})");

        public IClientProxy Group(string groupName)
            => _proxy($"Group({groupName})");

        public IClientProxy Groups(IReadOnlyList<string> groupNames)
            => _proxy($"Groups({string.Join(",", groupNames)})");

        public IClientProxy GroupExcept(string groupName, IReadOnlyList<string> excludedConnectionIds)
            => _proxy($"GroupExcept({groupName})");

        public IClientProxy User(string userId)
            => _proxy($"User({userId})");

        public IClientProxy Users(IReadOnlyList<string> userIds)
            => _proxy($"Users({string.Join(",", userIds)})");

        private IClientProxy _proxy(string target) => new RecordingClientProxy(target, _sink);
    }


    private sealed class RecordingGroupManager : IGroupManager
    {
        public Task AddToGroupAsync(
            string connectionId, string groupName, CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public Task RemoveFromGroupAsync(
            string connectionId, string groupName, CancellationToken cancellationToken = default)
            => Task.CompletedTask;
    }
}
