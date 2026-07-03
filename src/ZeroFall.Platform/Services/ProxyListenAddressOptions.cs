using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace ZeroFall.Platform.Services;

public static class ProxyListenAddressOptions
{
    public static IReadOnlyList<string> Build()
    {
        var options = new List<string> { "127.0.0.1", "0.0.0.0" };
        foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (nic.OperationalStatus != OperationalStatus.Up)
                continue;
            if (nic.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel)
                continue;

            foreach (var addr in nic.GetIPProperties().UnicastAddresses)
            {
                if (addr.Address.AddressFamily != AddressFamily.InterNetwork)
                    continue;
                if (IPAddress.IsLoopback(addr.Address))
                    continue;
                var ip = addr.Address.ToString();
                if (!options.Contains(ip))
                    options.Add(ip);
            }
        }

        return options;
    }
}
