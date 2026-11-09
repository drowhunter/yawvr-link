using System.Net;
using Microsoft.Extensions.Logging;

namespace YawVR.Link.Emulator;

public sealed class EmulatorOptions
{
    /// <summary>UDP port for discovery queries and motion (real devices: 50010). 0 picks a free port.</summary>
    public int UdpPort { get; set; } = 50010;

    /// <summary>TCP control port (real devices: 50020). 0 picks a free port.</summary>
    public int TcpPort { get; set; } = 50020;

    public IPAddress BindAddress { get; set; } = IPAddress.Any;

    /// <summary>How often telemetry is sent to checked-in senders. The Unity emulator used about 100 Hz.</summary>
    public double TelemetryRateHz { get; set; } = 100;

    /// <summary>Accept START, STOP and settings only from the checked-in game. Off by default, like the Unity emulator.</summary>
    public bool RequireOwnerForControl { get; set; }

    /// <summary>Drop motion datagrams unless the device has been started. Off by default: GameLink streams motion before START.</summary>
    public bool MotionRequiresStart { get; set; }

    /// <summary>Where callbacks run. Null runs them on the network thread.</summary>
    public SynchronizationContext? CallbackContext { get; set; }

    public ILoggerFactory? LoggerFactory { get; set; }
}

/// <summary>The emulator's session state.</summary>
public enum EmulatorState
{
    /// <summary>No game checked in.</summary>
    Available,

    /// <summary>A game is checked in; motion is stopped.</summary>
    Ready,

    /// <summary>START received.</summary>
    Running,
}
