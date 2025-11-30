using System;
using System.Collections;
using System.Runtime.InteropServices;

namespace Tools;

[ComImport]
[Guid("DCB00000-570F-4A9B-8D69-199FDBA5723B")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
public interface INetworkListManager
{
    [return: MarshalAs(UnmanagedType.Interface)]
    IEnumerable GetNetworks(NLM_ENUM_NETWORK Flags);

    [return: MarshalAs(UnmanagedType.Interface)]
    INetwork GetNetwork(Guid networkId);

    bool IsConnectedToInternet { get; }
    bool IsConnected { get; }
}

[ComImport]
[Guid("DCB00001-570F-4A9B-8D69-199FDBA5723B")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
public interface INetwork
{
    Guid GetNetworkId();
    NLM_CONNECTIVITY GetConnectivity();
    string GetName();
}

public enum NLM_ENUM_NETWORK
{
    NLM_ENUM_NETWORK_CONNECTED = 1,
    NLM_ENUM_NETWORK_DISCONNECTED = 2,
    NLM_ENUM_NETWORK_ALL = 3
}

public enum NLM_CONNECTIVITY
{
    NLM_CONNECTIVITY_DISCONNECTED = 0,
    NLM_CONNECTIVITY_IPV4_NOTRAFFIC = 1,
    NLM_CONNECTIVITY_IPV6_NOTRAFFIC = 2,
    NLM_CONNECTIVITY_IPV4_SUBNET = 4,
    NLM_CONNECTIVITY_IPV4_LOCALNETWORK = 8,
    NLM_CONNECTIVITY_IPV4_INTERNET = 16,
    NLM_CONNECTIVITY_IPV6_SUBNET = 32,
    NLM_CONNECTIVITY_IPV6_LOCALNETWORK = 64,
    NLM_CONNECTIVITY_IPV6_INTERNET = 128
}

[ComImport]
[Guid("DCB00000-570F-4A9B-8D69-199FDBA5723B")]
public class NetworkListManager
{
}

