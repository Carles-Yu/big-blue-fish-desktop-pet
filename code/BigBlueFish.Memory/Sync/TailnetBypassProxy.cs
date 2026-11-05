using System.Net;
using System.Net.Sockets;

namespace BigBlueFish.Memory.Sync;

// A system HTTP proxy may have no route into this device's tailnet. Keep public
// self-hosted endpoints on the user's proxy, but connect tailnet endpoints directly.
public sealed class TailnetBypassProxy(IWebProxy upstream) : IWebProxy
{
    public ICredentials? Credentials { get => upstream.Credentials; set => upstream.Credentials = value; }

    public Uri? GetProxy(Uri destination) => IsTailnet(destination) ? destination : upstream.GetProxy(destination);

    public bool IsBypassed(Uri destination) => IsTailnet(destination) || upstream.IsBypassed(destination);

    static bool IsTailnet(Uri destination)
    {
        var host = destination.Host.Trim('[', ']').TrimEnd('.');
        if (host.EndsWith(".ts.net", StringComparison.OrdinalIgnoreCase)) return true;
        if (!IPAddress.TryParse(host, out var address)) return false;
        var bytes = address.GetAddressBytes();
        if (address.AddressFamily == AddressFamily.InterNetwork)
            return bytes[0] == 100 && bytes[1] >= 64 && bytes[1] <= 127;
        return address.AddressFamily == AddressFamily.InterNetworkV6
            && bytes[0] == 0xfd && bytes[1] == 0x7a && bytes[2] == 0x11
            && bytes[3] == 0x5c && bytes[4] == 0xa1 && bytes[5] == 0xe0;
    }
}
