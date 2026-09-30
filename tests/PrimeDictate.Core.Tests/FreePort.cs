using System.Net;
using System.Net.Sockets;

namespace PrimeDictate.Core.Tests;

/// <summary>Asks the OS for an unused port instead of guessing one, which collided with busy ports under load.</summary>
internal static class FreePort
{
    public static int Next()
    {
        var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        try
        {
            return ((IPEndPoint)probe.LocalEndpoint).Port;
        }
        finally
        {
            probe.Stop();
        }
    }
}
