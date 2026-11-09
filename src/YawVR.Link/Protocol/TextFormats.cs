using System.Globalization;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;

namespace YawVR.Link.Protocol;

/// <summary>A parsed discovery reply: <c>&lt;model&gt;;&lt;id&gt;;&lt;name&gt;;&lt;tcpPort&gt;;&lt;availability&gt;</c>.</summary>
public sealed record DiscoveryReply(YawModel Model, string Id, string Name, int TcpPort, bool Reserved)
{
    public string ToWire() =>
        $"{ModelTag(Model)};{Id};{Name};{TcpPort};{(Reserved ? "RESERVED" : "AVAILABLE")}";

    public static string ModelTag(YawModel model) => model switch
    {
        YawModel.Yaw2 => "YAWDEVICE2",
        YawModel.Yaw3 => "YAWDEVICE3",
        _ => "YAWDEVICE",
    };

    /// <summary>Parses a reply the way GameLink does: field 0 model, 2 name, 4 availability.</summary>
    public static DiscoveryReply? Parse(string text)
    {
        if (text.Contains(TextFormats.DiscoveryQuery)) return null;
        var f = text.Split(';');
        if (f.Length < 5 || !f[0].StartsWith("YAWDEVICE", StringComparison.Ordinal)) return null;
        var model = f[0] switch { "YAWDEVICE2" => YawModel.Yaw2, "YAWDEVICE3" => YawModel.Yaw3, _ => YawModel.Yaw1 };
        int port = int.TryParse(f[3], out var p) ? p : 50020;
        return new DiscoveryReply(model, f[1], f[2], port, f[4].Trim() == "RESERVED");
    }
}

/// <summary>The ASCII formats carried over UDP.</summary>
public static class TextFormats
{
    public const string DiscoveryQuery = "YAW_CALLING";

    private static readonly Regex TelemetryField = new(@"(S|G)?([YPRU])\[(-?[0-9]+(?:\.[0-9]+)?)\]", RegexOptions.Compiled);

    /// <summary>
    /// Formats an angle the way both sources do: invariant culture, <c>000.000</c>, and values above 180
    /// shifted down by 360 once.
    /// </summary>
    public static string Angle(float degrees)
    {
        if (degrees > 180f) degrees -= 360f;
        return degrees.ToString("000.000", CultureInfo.InvariantCulture);
    }

    /// <summary>Motion datagram <c>Y[..]P[..]R[..]</c> with optional <c>V[..]</c> and <c>F[..]</c> suffixes.</summary>
    public static string Motion(Orientation pose, Vibration? vibration = null, byte? fan = null)
    {
        var sb = new StringBuilder();
        sb.Append("Y[").Append(Angle(pose.Yaw)).Append("]P[").Append(Angle(pose.Pitch)).Append("]R[").Append(Angle(pose.Roll)).Append(']');
        if (vibration is { } v)
            sb.Append(CultureInfo.InvariantCulture, $"V[{v.Amplitude0},{v.Amplitude1},{v.Amplitude2},{v.FrequencyHz}]");
        if (fan is { } f)
            sb.Append(CultureInfo.InvariantCulture, $"F[{f},{f}]");
        return sb.ToString();
    }

    /// <summary>Vibration-only datagram <c>V[a,a,a,hz]</c>.</summary>
    public static string VibrationOnly(Vibration v) =>
        string.Create(CultureInfo.InvariantCulture, $"V[{v.Amplitude0},{v.Amplitude1},{v.Amplitude2},{v.FrequencyHz}]");

    /// <summary>
    /// Parses a motion datagram. Accepts <c>Y[..]P[..]R[..]</c> with optional <c>V</c>/<c>F</c> suffixes,
    /// the SimTools form <c>YS[..]P[..]R[..]B[bits]</c>, and a bare <c>V[..]</c>. Returns null for anything else.
    /// </summary>
    public static MotionFrame? ParseMotion(string text, IPEndPoint source)
    {
        var fields = Fields(text);
        if (fields.Count == 0) return null;

        Vibration? vibration = null;
        if (fields.TryGetValue("V", out var vText))
        {
            var parts = vText.Split(',');
            if (parts.Length == 4 && parts.All(x => byte.TryParse(x, NumberStyles.Integer, CultureInfo.InvariantCulture, out _)))
            {
                var b = parts.Select(x => byte.Parse(x, CultureInfo.InvariantCulture)).ToArray();
                vibration = new Vibration(b[0], b[1], b[2], b[3]);
            }
        }

        IReadOnlyList<int>? fan = null;
        if (fields.TryGetValue("F", out var fText))
            fan = fText.Split(',').Select(x => int.TryParse(x, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) ? n : 0).ToArray();

        bool simTools = fields.ContainsKey("YS");
        string? yawText = simTools ? fields["YS"] : fields.GetValueOrDefault("Y");
        if (yawText is null || !fields.TryGetValue("P", out var pText) || !fields.TryGetValue("R", out var rText))
        {
            return vibration is null && fan is null
                ? null
                : new MotionFrame(Orientation.Zero, source) { Vibration = vibration, Fan = fan, HasPose = false };
        }

        if (!TryFloat(yawText, out var yaw) || !TryFloat(pText, out var pitch) || !TryFloat(rText, out var roll))
            return null;

        int? bitRange = null;
        if (simTools)
        {
            // The fourth bracket holds the bit range; its label is not checked (as in the emulator).
            var extra = fields.Where(kv => kv.Key is not ("YS" or "P" or "R" or "V" or "F")).Select(kv => kv.Value).FirstOrDefault();
            bitRange = extra is not null && int.TryParse(extra, out var bits) ? bits : 10;
            (yaw, pitch, roll) = (FromBitRange(yaw, bitRange.Value), FromBitRange(pitch, bitRange.Value), FromBitRange(roll, bitRange.Value));
        }

        return new MotionFrame(new Orientation(yaw, pitch, roll), source) { Vibration = vibration, Fan = fan, BitRange = bitRange };
    }

    /// <summary>Converts a SimTools bit-range value to degrees in -180..180 (centre = 2^bits/2 - 1 = 0°).</summary>
    public static float FromBitRange(float value, int bits)
    {
        float size = MathF.Pow(2, bits);
        float degrees = (value - (size / 2f - 1f)) * 360f / size;
        return degrees > 180f ? degrees - 360f : degrees;
    }

    /// <summary>Game telemetry <c>SY[..]SP[..]SR[..]</c>, with <c>U[volts]</c> appended when a battery voltage is given.</summary>
    public static string Telemetry(Orientation actual, float? batteryVoltage = null)
    {
        var s = $"SY[{Angle(actual.Yaw)}]SP[{Angle(actual.Pitch)}]SR[{Angle(actual.Roll)}]";
        if (batteryVoltage is { } u) s += $"U[{u.ToString("0.000", CultureInfo.InvariantCulture)}]";
        return s;
    }

    /// <summary>App telemetry: the actual pose followed by the commanded game pose.</summary>
    public static string AppTelemetry(Orientation actual, Orientation game) =>
        $"SY[{Angle(actual.Yaw)}]SP[{Angle(actual.Pitch)}]SR[{Angle(actual.Roll)}]GY[{Angle(game.Yaw)}]GP[{Angle(game.Pitch)}]GR[{Angle(game.Roll)}]";

    public static Telemetry? ParseTelemetry(string text)
    {
        float? sy = null, sp = null, sr = null, gy = null, gp = null, gr = null, u = null;
        foreach (Match m in TelemetryField.Matches(text))
        {
            if (!TryFloat(m.Groups[3].Value, out var v)) continue;
            bool game = m.Groups[1].Value == "G";
            switch (m.Groups[2].Value)
            {
                case "Y": if (game) gy = v; else sy = v; break;
                case "P": if (game) gp = v; else sp = v; break;
                case "R": if (game) gr = v; else sr = v; break;
                case "U": u = v; break;
            }
        }
        if (sy is null || sp is null || sr is null) return null;
        return new Telemetry(new Orientation(sy.Value, sp.Value, sr.Value))
        {
            BatteryVoltage = u,
            Game = gy is not null && gp is not null && gr is not null ? new Orientation(gy.Value, gp.Value, gr.Value) : null,
        };
    }

    /// <summary>Splits <c>A[x]B[y]</c> into label/value pairs. Unlabelled brackets get their position as label.</summary>
    private static Dictionary<string, string> Fields(string text)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        int i = 0, index = 0;
        while (i < text.Length)
        {
            int open = text.IndexOf('[', i);
            if (open < 0) break;
            int close = text.IndexOf(']', open + 1);
            if (close < 0) break;
            var label = text.Substring(i, open - i).Trim();
            if (label.Length == 0) label = $"#{index}";
            result.TryAdd(label, text.Substring(open + 1, close - open - 1));
            i = close + 1;
            index++;
        }
        return result;
    }

    private static bool TryFloat(string s, out float value) =>
        float.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out value);
}
