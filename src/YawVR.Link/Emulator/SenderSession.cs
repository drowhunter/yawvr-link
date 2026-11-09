using System.Net;
using System.Net.Sockets;
using YawVR.Link.Protocol;

namespace YawVR.Link.Emulator;

/// <summary>One TCP connection from a sender (a game, GameLink, SimHub or the Yaw app).</summary>
public sealed class SenderSession
{
    private readonly TcpClient client;
    private readonly NetworkStream stream;
    private readonly SemaphoreSlim writeLock = new(1, 1);

    internal SenderSession(TcpClient client, Func<YawModel> model)
    {
        this.client = client;
        stream = client.GetStream();
        Remote = (IPEndPoint)client.Client.RemoteEndPoint!;
        Framer = new TcpFramer(TcpDirection.SenderToDevice, model);
    }

    public IPEndPoint Remote { get; }
    public DateTimeOffset ConnectedAt { get; } = DateTimeOffset.UtcNow;

    /// <summary>Game name from CHECK_IN, or null before check-in and for app sessions.</summary>
    public string? GameName { get; internal set; }

    /// <summary>True when this session checked in with CHECK_IN_APP (the Yaw mobile app).</summary>
    public bool IsApp { get; internal set; }

    /// <summary>Where telemetry goes: the sender's IP and the port it asked for at check-in.</summary>
    public IPEndPoint? TelemetryEndpoint { get; internal set; }

    internal TcpFramer Framer { get; }
    internal NetworkStream Stream => stream;

    internal async Task SendAsync(byte[] bytes, CancellationToken ct)
    {
        await writeLock.WaitAsync(ct).ConfigureAwait(false);
        try { await stream.WriteAsync(bytes, ct).ConfigureAwait(false); }
        finally { writeLock.Release(); }
    }

    internal void Close()
    {
        try { client.Close(); } catch { /* already closed */ }
    }

    public override string ToString() => IsApp ? $"app@{Remote}" : $"{GameName ?? "(not checked in)"}@{Remote}";
}
