using System.Buffers.Binary;
using System.Text;

namespace YawVR.Link.Protocol;

/// <summary>
/// Encoders for every TCP record, in both directions. Integers and floats are 4-byte big-endian,
/// booleans one byte, strings ASCII without terminator.
/// </summary>
public static class Packets
{
    // ---------------------------------------------------------------- sender to device

    public static byte[] CheckIn(int telemetryPort, string gameName) =>
        Build(CommandIds.CheckIn, w => w.Int(telemetryPort).Ascii(gameName));

    public static byte[] CheckInApp(int telemetryPort) => Build(CommandIds.CheckInApp, w => w.Int(telemetryPort));

    public static byte[] Start() => new[] { CommandIds.Start };

    /// <summary>STOP as GameLink sends it, with a park flag.</summary>
    public static byte[] Stop(bool park) => new[] { CommandIds.Stop, (byte)(park ? 1 : 0) };

    public static byte[] Exit() => new[] { CommandIds.Exit };

    public static byte[] SetPower(int percent) => Build(CommandIds.SetPower, w => w.Int(Math.Clamp(percent, 0, 99)));

    /// <summary>Tilt limits in GameLink's order: backward, forward, roll.</summary>
    public static byte[] SetTiltLimits(TiltLimits limits) =>
        Build(CommandIds.SetTiltLimits, w => w.Int(limits.PitchBackward).Int(limits.PitchForward).Int(limits.Roll));

    public static byte[] SetYawLimit(int degrees) => Build(CommandIds.SetYawLimit, w => w.Int(degrees));

    public static byte[] SetYawReturnLimit(float value) => Build(CommandIds.SetYawReturnLimit, w => w.Float(value));

    public static byte[] SetHasYawLimit(bool enabled) => new[] { CommandIds.SetHasYawLimit, Bool(enabled) };

    /// <summary>Vibration enable. Yaw1 has no limit byte.</summary>
    public static byte[] SetHasVibration(YawModel model, bool enabled, byte limit) => model == YawModel.Yaw1
        ? new[] { CommandIds.SetHasVibration, Bool(enabled) }
        : new[] { CommandIds.SetHasVibration, Bool(enabled), limit };

    public static byte[] Calibrate(bool allAxes) => new[] { CommandIds.Calibrate, Bool(allAxes) };
    public static byte[] SaveSettings() => new[] { CommandIds.SaveSettings };
    public static byte[] Reboot() => new[] { CommandIds.Reboot, (byte)1 };
    public static byte[] DebugRegister(bool register, bool debugMode) => new[] { CommandIds.DebugRegister, Bool(register), Bool(debugMode) };
    public static byte[] LedEffects(ReadOnlySpan<byte> data) => Prefix(CommandIds.LedEffects, data);

    public static byte[] GetAllParameters() => new[] { CommandIds.GetAllParameters };
    public static byte[] GetState() => new[] { CommandIds.GetState };
    public static byte[] GetTemperatures() => new[] { CommandIds.GetTemperatures };
    public static byte[] GetSystemInfo() => new[] { CommandIds.SystemInfo };

    /// <summary>SIM_OFFSET request: <paramref name="set"/> false reads the offset, true writes it.</summary>
    public static byte[] SimOffset(bool set, Orientation offset) =>
        Build(CommandIds.SimOffset, w => w.Byte(Bool(set)).Float(offset.Yaw).Float(offset.Pitch).Float(offset.Roll));

    // ---------------------------------------------------------------- device to sender

    /// <summary>
    /// Check-in answer. GameLink splits it on ';' and reads field 2 as the edition, so an available device
    /// answers <c>AVAILABLE;&lt;id&gt;;&lt;EDITION&gt;</c>. The real device's field 1 is unconfirmed.
    /// </summary>
    public static byte[] CheckInAnswer(CheckInResponse response, DeviceInfo info) =>
        Prefix(CommandIds.CheckInAnswer, Encoding.ASCII.GetBytes(response.Available
            ? $"AVAILABLE;{info.Id};{info.Edition.ToString().ToUpperInvariant()}"
            : $"RESERVED;{response.ReservedGame};{response.ReservedIp}"));

    /// <summary>App check-in answer: ID, total length byte, then <c>&lt;availability&gt;;200;0;&lt;mode&gt;;&lt;game&gt;[;YAWDEVICE2]</c>.</summary>
    public static byte[] CheckInAppAnswer(bool reserved, bool simulationMode, string gameName, YawModel model)
    {
        string text = (reserved ? "RESERVED" : "AVAILABLE") + ";200;0;" + (simulationMode ? "simulation mode" : "disabled")
                      + ";" + gameName + (model == YawModel.Yaw2 ? ";YAWDEVICE2" : model == YawModel.Yaw3 ? ";YAWDEVICE3" : "");
        var body = Encoding.ASCII.GetBytes(text);
        var bytes = new byte[body.Length + 2];
        bytes[0] = CommandIds.CheckInAppAnswer;
        bytes[1] = (byte)Math.Min(255, bytes.Length);
        body.CopyTo(bytes, 2);
        return bytes;
    }

    /// <summary>Echo of START, STOP or EXIT: the bare command byte.</summary>
    public static byte[] Ack(byte id) => new[] { id };

    /// <summary>
    /// The GET_ALL_PARAMETERS dump: the setting echo records back to back, then the LED configuration.
    /// Offsets match GameLink's parser (power at 4, pitch back 9, forward 13, roll 17, yaw limit 19, ...).
    /// </summary>
    public static byte[] ParameterDump(DeviceParameters p, YawModel model)
    {
        var w = new Writer();
        w.Bytes(SetPower(p.Power));
        w.Bytes(SetTiltLimits(p.TiltLimits));
        w.Bytes(SetYawLimit(p.YawLimit));
        w.Bytes(SetHasYawLimit(p.HasYawLimit));
        w.Bytes(SetYawReturnLimit(p.YawReturnLimit));
        // Always three bytes inside the dump, so the LED config starts at offset 34 on every model.
        w.Bytes(new[] { CommandIds.SetHasVibration, Bool(p.HasVibration), p.VibrationLimit });
        if (model != YawModel.Yaw3)
        {
            int ledLength = model == YawModel.Yaw1 ? 34 : 37;
            var led = new byte[ledLength];
            p.LedConfig.AsSpan(0, Math.Min(ledLength, p.LedConfig.Length)).CopyTo(led);
            w.Byte(CommandIds.LedConfig).Bytes(led);
        }
        return w.ToArray();
    }

    /// <summary>GET_STATE reply: ID, total length byte, then <c>&lt;state&gt;;0;0;&lt;occupied&gt;;&lt;calibrating&gt;</c>.</summary>
    public static byte[] StateAnswer(DeviceStatus status)
    {
        var body = Encoding.ASCII.GetBytes(
            $"{DeviceStatus.ToWire(status.State)};0;0;{(status.Occupied ? 1 : 0)};{(status.Calibrating ? 1 : 0)}");
        var bytes = new byte[body.Length + 2];
        bytes[0] = CommandIds.GetState;
        bytes[1] = (byte)Math.Min(255, bytes.Length);
        body.CopyTo(bytes, 2);
        return bytes;
    }

    public static byte[] TemperaturesAnswer(Temperatures t)
    {
        var bytes = new byte[4];
        bytes[0] = CommandIds.GetTemperatures;
        for (int i = 0; i < 3 && i < t.Values.Count; i++) bytes[i + 1] = t.Values[i];
        return bytes;
    }

    public static byte[] SystemInfoAnswer(SystemInfo info) => Prefix(CommandIds.SystemInfo, Encoding.ASCII.GetBytes(info.Raw));

    public static byte[] SimOffsetAnswer(Orientation offset) =>
        Build(CommandIds.SimOffset, w => w.Float(offset.Yaw).Float(offset.Pitch).Float(offset.Roll));

    public static byte[] ButtonPressed(ArcadeEvent evt) => new[] { CommandIds.ButtonPressed, (byte)evt };

    public static byte[] Error(string message) => Prefix(CommandIds.Error, Encoding.ASCII.GetBytes(message));

    // ---------------------------------------------------------------- UDP LEDs

    /// <summary>Single colour LED datagram <c>[B2, G, R, B]</c>.</summary>
    public static byte[] LedColor(YawColor c) => new[] { CommandIds.UdpLedPrefix, c.G, c.R, c.B };

    /// <summary>Buffered LED datagram <c>[B2, h1, h2, G, R, B, ...]</c>.</summary>
    public static byte[] LedStrip(byte header1, byte header2, ReadOnlySpan<YawColor> colors)
    {
        var bytes = new byte[3 + colors.Length * 3];
        bytes[0] = CommandIds.UdpLedPrefix;
        bytes[1] = header1;
        bytes[2] = header2;
        for (int i = 0; i < colors.Length; i++)
        {
            bytes[3 + i * 3] = colors[i].G;
            bytes[4 + i * 3] = colors[i].R;
            bytes[5 + i * 3] = colors[i].B;
        }
        return bytes;
    }

    // ---------------------------------------------------------------- helpers

    private static byte Bool(bool value) => (byte)(value ? 1 : 0);

    private static byte[] Prefix(byte id, ReadOnlySpan<byte> payload)
    {
        var bytes = new byte[payload.Length + 1];
        bytes[0] = id;
        payload.CopyTo(bytes.AsSpan(1));
        return bytes;
    }

    private static byte[] Build(byte id, Action<Writer> body)
    {
        var w = new Writer().Byte(id);
        body(w);
        return w.ToArray();
    }

    private sealed class Writer
    {
        private readonly List<byte> bytes = new();

        public Writer Byte(byte b) { bytes.Add(b); return this; }
        public Writer Bytes(ReadOnlySpan<byte> b) { foreach (var x in b) bytes.Add(x); return this; }
        public Writer Ascii(string s) => Bytes(Encoding.ASCII.GetBytes(s));

        public Writer Int(int value)
        {
            Span<byte> tmp = stackalloc byte[4];
            BinaryPrimitives.WriteInt32BigEndian(tmp, value);
            return Bytes(tmp);
        }

        public Writer Float(float value)
        {
            Span<byte> tmp = stackalloc byte[4];
            BinaryPrimitives.WriteSingleBigEndian(tmp, value);
            return Bytes(tmp);
        }

        public byte[] ToArray() => bytes.ToArray();
    }
}
