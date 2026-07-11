using System.Net;

namespace SvnFlux.RaSvn.Server;

public sealed record SvnServerOptions
{
    public IPAddress Address { get; init; } = IPAddress.Loopback;
    public int Port { get; init; } = 3690;
    public int MaximumConcurrentSessions { get; init; } = 64;
    public Action<string>? DiagnosticLog { get; init; }
    public Action<string>? ProtocolTrace { get; init; }
}
