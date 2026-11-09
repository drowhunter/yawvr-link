using System.Diagnostics;
using System.Net;
using System.Text.RegularExpressions;

namespace YawVR.Link;

/// <summary>Simulator model, as advertised in discovery (<c>YAWDEVICE</c>, <c>YAWDEVICE2</c>, <c>YAWDEVICE3</c>).</summary>
public enum YawModel { Yaw1, Yaw2, Yaw3 }

/// <summary>Edition reported in the check-in answer. GameLink parses it with <c>Enum.TryParse</c>.</summary>
public enum DeviceEdition { Standard, Pro, Arcade }

/// <summary>Rotation in degrees.</summary>
[DebuggerDisplay("Y {Yaw:0.000} P {Pitch:0.000} R {Roll:0.000}")]
public readonly record struct Orientation(float Yaw, float Pitch, float Roll)
{
    public static Orientation Zero => default;
    public override string ToString() => $"Y {Yaw:0.000} P {Pitch:0.000} R {Roll:0.000}";
}

/// <summary>Tilt limits in degrees.</summary>
public readonly record struct TiltLimits(int PitchForward, int PitchBackward, int Roll);

/// <summary>An LED colour. The wire order is green, red, blue; this type hides that.</summary>
public readonly record struct YawColor(byte R, byte G, byte B);

/// <summary>Vibration suffix of a motion datagram, <c>V[a0,a1,a2,hz]</c>.</summary>
public readonly record struct Vibration(byte Amplitude0, byte Amplitude1, byte Amplitude2, byte FrequencyHz)
{
    public Vibration(byte amplitude, byte frequencyHz) : this(amplitude, amplitude, amplitude, frequencyHz) { }
}

/// <summary>Arcade button and seat events (GameLink's <c>ARCADE_EVENT_TYPE</c>).</summary>
public enum ArcadeEvent : byte { GreenPressed = 0, RedPressed = 1, GetIn = 2, GetOut = 3 }

/// <summary>Identity an emulator advertises in discovery, check-in and system info.</summary>
public sealed record DeviceInfo(YawModel Model, string Id, string Name)
{
    public DeviceEdition Edition { get; init; } = DeviceEdition.Standard;

    /// <summary>Whether a VirtualHere USB server is reported active in system info.</summary>
    public bool VirtualHereActive { get; init; }

    /// <summary>Raspberry Pi firmware version reported in system info (GameLink reads it for Yaw3).</summary>
    public int? RpiVersion { get; init; }
}

/// <summary>All settings a device reports in its parameter dump and setting echoes.</summary>
public sealed class DeviceParameters
{
    /// <summary>Maximum power, 0 to 99.</summary>
    public int Power { get; set; } = 50;
    public TiltLimits TiltLimits { get; set; } = new(30, 30, 30);
    public int YawLimit { get; set; } = 180;
    public bool HasYawLimit { get; set; }
    public float YawReturnLimit { get; set; }
    public bool HasVibration { get; set; }
    public byte VibrationLimit { get; set; }

    /// <summary>Raw LED configuration bytes from the end of the dump (34 bytes on Yaw1, 37 on Yaw2, none on Yaw3).</summary>
    public byte[] LedConfig { get; set; } = Array.Empty<byte>();

    public DeviceParameters Clone()
    {
        var copy = (DeviceParameters)MemberwiseClone();
        copy.LedConfig = (byte[])LedConfig.Clone();
        return copy;
    }

    /// <summary>Default limits GameLink applies when it selects a model.</summary>
    public static DeviceParameters DefaultsFor(YawModel model) => model switch
    {
        YawModel.Yaw1 => new() { TiltLimits = new(35, 35, 35), LedConfig = new byte[34] },
        YawModel.Yaw2 => new() { TiltLimits = new(15, 55, 20), LedConfig = new byte[37] },
        _ => new() { TiltLimits = new(25, 40, 20) },
    };
}

/// <summary>Operating state strings from the GET_STATE reply.</summary>
public enum DeviceStateKind { Disabled, SimulationMode, EmergencyMode, Parking, EmergencySwitch, Unknown }

/// <summary>Reply to GET_STATE: <c>&lt;state&gt;;?;?;&lt;occupied&gt;;&lt;calibrating&gt;</c>.</summary>
public sealed record DeviceStatus(DeviceStateKind State, bool Occupied = false, bool Calibrating = false)
{
    /// <summary>Raw text as received, when this status came off the wire.</summary>
    public string? Raw { get; init; }

    internal static string ToWire(DeviceStateKind kind) => kind switch
    {
        DeviceStateKind.Disabled => "disabled",
        DeviceStateKind.SimulationMode => "simulation mode",
        DeviceStateKind.EmergencyMode => "emergency mode",
        DeviceStateKind.Parking => "parking",
        DeviceStateKind.EmergencySwitch => "emergency switch",
        _ => "unknown",
    };

    internal static DeviceStateKind FromWire(string text) => text.Trim() switch
    {
        "disabled" => DeviceStateKind.Disabled,
        "simulation mode" => DeviceStateKind.SimulationMode,
        "emergency mode" => DeviceStateKind.EmergencyMode,
        "parking" => DeviceStateKind.Parking,
        "emergency switch" => DeviceStateKind.EmergencySwitch,
        _ => DeviceStateKind.Unknown,
    };
}

/// <summary>Reply to GET_TEMPS. GameLink reads three bytes.</summary>
public sealed record Temperatures(IReadOnlyList<byte> Values)
{
    public byte Max => Values.Count == 0 ? (byte)0 : Values.Max();
}

/// <summary>Reply to SYSTEM_INFO: free ASCII text with <c>key:value</c> lines.</summary>
public sealed record SystemInfo(string Raw)
{
    public bool? VirtualHereActive
    {
        get
        {
            var m = Regex.Match(Raw, "VirtualHere:(.*)");
            return m.Success ? m.Groups[1].Value.Trim() == "active" : null;
        }
    }

    public int? RpiVersion
    {
        get
        {
            var m = Regex.Match(Raw, "RPIVER:([0-9]+)");
            return m.Success ? int.Parse(m.Groups[1].Value) : null;
        }
    }
}

/// <summary>One motion datagram received by an emulator.</summary>
public sealed record MotionFrame(Orientation Pose, IPEndPoint Source)
{
    public Vibration? Vibration { get; init; }

    /// <summary>Values of the <c>F[..]</c> fan or smart-plug suffix, if present.</summary>
    public IReadOnlyList<int>? Fan { get; init; }

    /// <summary>Bit range of a SimTools <c>YS[..]</c> datagram. <see cref="Pose"/> is already converted to degrees.</summary>
    public int? BitRange { get; init; }

    /// <summary>False for a vibration-only datagram, which carries no pose.</summary>
    public bool HasPose { get; init; } = true;
}

/// <summary>A binary LED datagram (first byte 0xB2) received by an emulator.</summary>
public sealed record LedFrame(ReadOnlyMemory<byte> Raw, IPEndPoint Source)
{
    /// <summary>True for the 4-byte single colour form <c>[B2, G, R, B]</c>.</summary>
    public bool IsSingleColor => Raw.Length == 4;

    /// <summary>The two header bytes of a buffered frame (counter on Yaw1, unit and segment ids on Yaw2).</summary>
    public (byte, byte) Header => Raw.Length >= 3 && !IsSingleColor ? (Raw.Span[1], Raw.Span[2]) : ((byte)0, (byte)0);

    public IReadOnlyList<YawColor> Colors
    {
        get
        {
            var span = Raw.Span;
            int start = IsSingleColor ? 1 : 3;
            var list = new List<YawColor>(Math.Max(0, (span.Length - start) / 3));
            for (int i = start; i + 2 < span.Length; i += 3)
                list.Add(new YawColor(span[i + 1], span[i], span[i + 2]));
            return list;
        }
    }
}

/// <summary>Pose a device reports back over UDP.</summary>
public sealed record Telemetry(Orientation Actual)
{
    public float? BatteryVoltage { get; init; }

    /// <summary>Commanded game pose, only in app telemetry (<c>GY/GP/GR</c>).</summary>
    public Orientation? Game { get; init; }
}

/// <summary>A game check-in received by an emulator.</summary>
public sealed record CheckInRequest(string GameName, int TelemetryPort, IPEndPoint Remote);

/// <summary>An app (Yaw mobile app) check-in received by an emulator.</summary>
public sealed record AppCheckInRequest(int TelemetryPort, IPEndPoint Remote);

/// <summary>An emulator's answer to a game check-in.</summary>
public sealed record CheckInResponse(bool Available, string? ReservedGame = null, string? ReservedIp = null)
{
    public static CheckInResponse Accept() => new(true);
    public static CheckInResponse Reserved(string game, string ip) => new(false, game, ip);
}

/// <summary>The sender's view of a check-in answer.</summary>
public sealed record CheckInResult(bool Available, DeviceEdition Edition, string Raw)
{
    public string? ReservedBy { get; init; }
    public string? ReservedIp { get; init; }
}

/// <summary>Why a TCP session ended.</summary>
public enum DisconnectReason { Exit, RemoteClosed, LocalStop, Error }
