using Microsoft.Extensions.Hosting;
using System.Net.NetworkInformation;
using System.Threading;
using System.Threading.Tasks;

namespace Tools;

public class NetworkIdentifierHostedService : BackgroundService, INetworkIdentifier
{
    private string _lastNetwork;

    public event EventHandler<NetworkChangedEventArgs> NetworkChanged;

    public string GetCurrentNetwork()
    {
        // Reuse the cross-platform detection logic from before
        return CrossPlatformNetworkIdentifier.GetCurrentNetwork();
    }

    protected override Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Initial detection
        _lastNetwork = GetCurrentNetwork();

        // Subscribe to OS-specific events
        NetworkChange.NetworkAddressChanged += OnNetworkAddressChanged;

        // Keep service alive
        return Task.CompletedTask;
    }

    private void OnNetworkAddressChanged(object sender, EventArgs e)
    {
        var current = GetCurrentNetwork();
        if (current != _lastNetwork)
        {
            _lastNetwork = current;
            NetworkChanged?.Invoke(this, new NetworkChangedEventArgs(current));
        }
    }

    public override Task StopAsync(CancellationToken cancellationToken)
    {
        NetworkChange.NetworkAddressChanged -= OnNetworkAddressChanged;
        return base.StopAsync(cancellationToken);
    }
}