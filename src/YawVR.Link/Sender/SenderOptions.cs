using System.Net;
using Microsoft.Extensions.Logging;
using YawVR.Link.Protocol;

namespace YawVR.Link.Sender;

public sealed class SenderOptions
{
    /// <summary>Name sent in CHECK_IN; shows up as the reserving game on the device.</summary>
    public string GameName { get; set; } = "YawVR.Link";

    /// <summary>Local UDP port for telemetry from the device (GameLink: 50011). 0 picks a free port.</summary>
    public int TelemetryPort { get; set; } = 50011;

    /// <summary>Local UDP port discovery replies come back to (GameLink: 50050). 0 picks a free port.</summary>
    public int DiscoveryReplyPort { get; set; } = 50050;

    /// <summary>Device UDP port for discovery and motion (50010 on real devices).</summary>
    public int DeviceUdpPort { get; set; } = 50010;

    /// <summary>
    /// Where discovery queries go. Empty (the default) broadcasts on every IPv4 interface, as GameLink does.
    /// Set explicit addresses to reach devices on other subnets or on localhost.
    /// </summary>
    public IList<IPAddress> DiscoveryTargets { get; } = new List<IPAddress>();

    /// <summary>How often motion is sent while streaming. GameLink sends about 50 Hz.</summary>
    public double MotionRateHz { get; set; } = 50;

    /// <summary>Stream motion right after check-in instead of waiting for <see cref="YawSender.StartMotionAsync"/>.</summary>
    public bool StreamMotionOnConnect { get; set; }

    /// <summary>Poll GET_STATE and GET_TEMPS every second, as GameLink does.</summary>
    public bool PollStatus { get; set; } = true;

    /// <summary>Send SAVE_SETTINGS on the next poll after any setting change, as GameLink does.</summary>
    public bool AutoSave { get; set; } = true;

    /// <summary>Ask for system info and parameters right after check-in.</summary>
    public bool ReadDeviceOnConnect { get; set; } = true;

    /// <summary>How long awaitable requests wait for their reply.</summary>
    public TimeSpan RequestTimeout { get; set; } = TimeSpan.FromSeconds(2);

    /// <summary>Where callbacks run. Null runs them on the network thread.</summary>
    public SynchronizationContext? CallbackContext { get; set; }

    public ILoggerFactory? LoggerFactory { get; set; }
}

/// <summary>A device that answered discovery.</summary>
public sealed record DiscoveredDevice(IPAddress Address, DiscoveryReply Reply)
{
    public YawModel Model => Reply.Model;
    public string Name => Reply.Name;
    public bool Reserved => Reply.Reserved;
    public int TcpPort => Reply.TcpPort;
    public DateTimeOffset LastSeen { get; internal set; } = DateTimeOffset.UtcNow;

    public override string ToString() => $"{Name} ({Model}) at {Address}{(Reserved ? ", reserved" : "")}";
}

/// <summary>The device is checked in by another game.</summary>
public sealed class DeviceReservedException : Exception
{
    public DeviceReservedException(CheckInResult result)
        : base($"Device is reserved by '{result.ReservedBy}' at {result.ReservedIp}.") => Result = result;

    public CheckInResult Result { get; }
}
