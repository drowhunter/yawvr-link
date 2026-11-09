using System.Buffers.Binary;

namespace YawVR.Link.Protocol;

/// <summary>Which way a TCP stream flows; command IDs mean different things each way.</summary>
public enum TcpDirection { SenderToDevice, DeviceToSender }

/// <summary>One complete TCP command or reply, including its ID byte.</summary>
public readonly struct TcpRecord
{
    public TcpRecord(byte[] bytes) => Bytes = bytes;

    /// <summary>The whole record, ID byte first.</summary>
    public byte[] Bytes { get; }
    public byte Id => Bytes[0];
    public ReadOnlySpan<byte> Payload => Bytes.AsSpan(1);
    public int Length => Bytes.Length;

    public int ReadInt32(int offset) => BinaryPrimitives.ReadInt32BigEndian(Bytes.AsSpan(offset, 4));
    public float ReadSingle(int offset) => BinaryPrimitives.ReadSingleBigEndian(Bytes.AsSpan(offset, 4));
    public bool ReadBool(int offset) => Bytes.Length > offset && Bytes[offset] != 0;
    public string ReadAscii(int offset) => Bytes.Length > offset ? System.Text.Encoding.ASCII.GetString(Bytes, offset, Bytes.Length - offset) : "";

    public override string ToString() => $"{CommandIds.NameOf(Id)} [{Convert.ToHexString(Bytes)}]";
}

/// <summary>
/// Splits a TCP byte stream into records. The protocol has no length prefix, so each ID has a length rule:
/// a fixed size, an ASCII tail (printable bytes up to the first non-printable one), a length byte, or
/// "rest of what has arrived" for payloads nobody has documented. GameLink and the Unity emulator both
/// assumed one socket read is one command; this framer copes with split and merged reads.
/// </summary>
public sealed class TcpFramer
{
    private readonly TcpDirection direction;
    private readonly Func<YawModel> model;
    private byte[] buffer = new byte[1024];
    private int count;

    public TcpFramer(TcpDirection direction, Func<YawModel> model)
    {
        this.direction = direction;
        this.model = model;
    }

    /// <summary>
    /// Set while a GET_ALL_PARAMETERS dump is arriving. Inside the dump, SET_HAS_VIBRATION always carries
    /// the vibration-limit byte, even on a Yaw1.
    /// </summary>
    public bool InParameterDump { get; set; }

    public int Buffered => count;

    public void Append(ReadOnlySpan<byte> data)
    {
        if (count + data.Length > buffer.Length)
            Array.Resize(ref buffer, Math.Max(buffer.Length * 2, count + data.Length));
        data.CopyTo(buffer.AsSpan(count));
        count += data.Length;
    }

    /// <summary>Takes the next complete record off the buffer, or returns false if more bytes are needed.</summary>
    public bool TryRead(out TcpRecord record)
    {
        record = default;
        if (count == 0) return false;

        var span = buffer.AsSpan(0, count);
        int length = RecordLength(span);
        if (length <= 0 || length > count) return false;

        record = new TcpRecord(span[..length].ToArray());
        buffer.AsSpan(length, count - length).CopyTo(buffer);
        count -= length;
        return true;
    }

    public IEnumerable<TcpRecord> ReadAll()
    {
        while (TryRead(out var r)) yield return r;
    }

    /// <summary>Length of the record at the start of <paramref name="span"/>, or 0 if it is not complete yet.</summary>
    private int RecordLength(ReadOnlySpan<byte> span)
    {
        byte id = span[0];
        return direction == TcpDirection.SenderToDevice ? ToDeviceLength(id, span) : ToSenderLength(id, span);
    }

    private int ToDeviceLength(byte id, ReadOnlySpan<byte> s) => id switch
    {
        CommandIds.CheckIn => AsciiTail(s, 5),
        CommandIds.CheckInApp => 5,
        CommandIds.Start or CommandIds.Exit => 1,
        CommandIds.Stop => StopLength(s),
        CommandIds.SetPower or CommandIds.SetYawLimit or CommandIds.SetYawReturnLimit => 5,
        CommandIds.SetTiltLimits => 13,
        CommandIds.SetHasYawLimit => 2,
        CommandIds.SetHasVibration => model() == YawModel.Yaw1 ? 2 : 3,
        CommandIds.Calibrate or CommandIds.Reboot => 2,
        CommandIds.DebugRegister => 3,
        CommandIds.SimOffset => 14,
        CommandIds.GetAllParameters or CommandIds.GetState or CommandIds.GetTemperatures
            or CommandIds.SystemInfo or CommandIds.SaveSettings or CommandIds.ResetPorts
            or CommandIds.GetGameParams or CommandIds.GetPidParams or CommandIds.GetModes => 1,
        // Catalogued commands with sizes taken from the emulator's encoders.
        CommandIds.SetSimuInputPort or CommandIds.SetGameInputPort or CommandIds.SetOutputPort
            or CommandIds.SetYawReachLimit or CommandIds.SetYawLimitSpeed => 5,
        CommandIds.SetFan => 2,
        CommandIds.SetYawPid or CommandIds.SetPitchPid or CommandIds.SetRollPid => 13,
        CommandIds.SetPidParams => 37,
        CommandIds.SetGameIpAddress or CommandIds.SetGameMode or CommandIds.SetLedStripMode => AsciiTail(s, 1),
        _ => s.Length, // LED effects and anything unknown: whatever has arrived
    };

    private int ToSenderLength(byte id, ReadOnlySpan<byte> s) => id switch
    {
        CommandIds.CheckInAnswer => AsciiTail(s, 1),
        CommandIds.CheckInAppAnswer => s.Length >= 2 ? Math.Max(2, (int)s[1]) : 0,
        CommandIds.Start or CommandIds.Exit => 1,
        CommandIds.Stop => StopLength(s),
        CommandIds.SetPower or CommandIds.SetYawLimit or CommandIds.SetYawReturnLimit => 5,
        CommandIds.SetTiltLimits => 13,
        CommandIds.SetHasYawLimit => 2,
        CommandIds.SetHasVibration => model() == YawModel.Yaw1 && !InParameterDump ? 2 : 3,
        CommandIds.GetState => AsciiTail(s, 2),
        CommandIds.GetTemperatures => 4,
        CommandIds.SimOffset => 13,
        CommandIds.ButtonPressed => 2,
        CommandIds.SystemInfo or CommandIds.Error => AsciiTail(s, 1),
        _ => s.Length, // LED config record and anything unknown
    };

    // GameLink sends STOP with a park byte; the Unity emulator sends and expects a bare byte.
    private static int StopLength(ReadOnlySpan<byte> s) => s.Length >= 2 && s[1] <= 1 ? 2 : 1;

    /// <summary>Header of <paramref name="headerLength"/> bytes followed by printable ASCII.</summary>
    private static int AsciiTail(ReadOnlySpan<byte> s, int headerLength)
    {
        if (s.Length < headerLength) return 0;
        int i = headerLength;
        while (i < s.Length && IsText(s[i])) i++;
        return i;
    }

    private static bool IsText(byte b) => b is >= 0x20 and < 0x7F or (byte)'\r' or (byte)'\n' or (byte)'\t';
}
