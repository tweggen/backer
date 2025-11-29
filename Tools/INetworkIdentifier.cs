namespace Tools;

public interface INetworkIdentifier
{
    string GetCurrentNetwork();
    event EventHandler<NetworkChangedEventArgs> NetworkChanged;
}
