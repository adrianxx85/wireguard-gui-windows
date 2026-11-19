using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace WGClient.Core;

public static class LocalNetworks
{
    /// <summary>IPv4-Netze aller aktiven Netzwerkadapter (z. B. "192.168.178.0/24").</summary>
    public static List<string> IPv4Subnets()
    {
        var result = new List<string>();
        try
        {
            foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (nic.OperationalStatus != OperationalStatus.Up || nic.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
                foreach (var ua in nic.GetIPProperties().UnicastAddresses)
                {
                    if (ua.Address.AddressFamily != AddressFamily.InterNetwork || IPAddress.IsLoopback(ua.Address)) continue;
                    if (ua.PrefixLength is >= 8 and <= 32) result.Add(Cidr.NetworkV4(ua.Address, ua.PrefixLength));
                }
            }
        }
        catch (NetworkInformationException) { /* ohne Adapterliste gibt es eben keine Warnungen */ }
        return result.Distinct().ToList();
    }
}
