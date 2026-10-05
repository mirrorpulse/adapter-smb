using System.Net;
using System.Net.NetworkInformation;

namespace MirrorPulse.Adapter.Smb.Worker;

internal static class SmbServerIdentity
{
    public static async Task<bool> IsLocalAsync(string networkPath, CancellationToken token)
    {
        string server = networkPath[2..].Split('\\')[0];
        if (server.Equals("localhost", StringComparison.OrdinalIgnoreCase) ||
            server.Equals(Environment.MachineName, StringComparison.OrdinalIgnoreCase)) return true;
        IPAddress[] addresses = await Dns.GetHostAddressesAsync(server, token).ConfigureAwait(false);
        HashSet<IPAddress> local = NetworkInterface.GetAllNetworkInterfaces()
            .SelectMany(adapter => adapter.GetIPProperties().UnicastAddresses)
            .Select(address => address.Address).ToHashSet();
        bool localServer = addresses.Any(address => IPAddress.IsLoopback(address) || local.Contains(address));
        if (localServer && addresses.Any(address => !IPAddress.IsLoopback(address) && !local.Contains(address)))
            throw new InvalidDataException("AmbiguousServerIdentity");
        return localServer;
    }
}
