using System.Net;
using System.Net.Sockets;

// Ports for stub servers that Core is told to start later. Asking the OS for
// port 0 and closing it hands out an ephemeral port, which the OS gives the
// next test's connection or listener too: with tests in parallel, another
// test sometimes had it by the time the stub started. These come from below
// the ephemeral range (Linux starts it at 32768), each once per test run,
// and only when they can be bound now.
public static class TestPorts
{
    private static int _next = 20000 + Environment.ProcessId % 500 * 20;

    public static int Next()
    {
        for (var tries = 0; tries < 2000; tries++)
        {
            var port = Interlocked.Increment(ref _next);
            if (port >= 32000) Interlocked.Exchange(ref _next, 20000);
            try
            {
                var listener = new TcpListener(IPAddress.Loopback, port);
                listener.Start();
                listener.Stop();
                return port;
            }
            catch (SocketException) { }
        }
        throw new InvalidOperationException("No free test port below the ephemeral range.");
    }
}
