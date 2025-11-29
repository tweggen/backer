namespace Tools;

public class NetworkChangedEventArgs : EventArgs
{
    public string NetworkName { get; }
    public NetworkChangedEventArgs(string networkName) => NetworkName = networkName;
}