namespace YawVR.Link.Protocol;

/// <summary>
/// One-byte TCP command identifiers. Values come from GameLink's and the Unity emulator's
/// <c>CommandIds</c> catalogs. Several IDs are only catalogued; nobody has confirmed their payloads.
/// </summary>
public static class CommandIds
{
    // Session
    public const byte CheckIn = 0x30;
    public const byte CheckInAnswer = 0x31;
    public const byte CheckInApp = 0x33;
    public const byte CheckInAppAnswer = 0x34;
    public const byte Start = 0xA1;
    public const byte Stop = 0xA2;
    public const byte Exit = 0xA3;

    // Settings (each is echoed back by the device)
    public const byte SetPower = 0x32;
    public const byte SetTiltLimits = 0x40;
    public const byte SetYawLimit = 0x70;
    public const byte SetYawReturnLimit = 0x64;
    public const byte SetHasYawLimit = 0x62;
    public const byte SetHasVibration = 0x61;

    // Actions
    public const byte Calibrate = 0x55;
    public const byte SaveSettings = 0xB0;
    public const byte Reboot = 0xB2;
    public const byte DebugRegister = 0xE6;
    public const byte LedEffects = 0xD2;

    // Queries
    public const byte GetAllParameters = 0xF6;
    public const byte GetState = 0xE5;
    public const byte GetTemperatures = 0xE4;
    public const byte SystemInfo = 0xD4;
    public const byte SimOffset = 0xE7;

    // Device to sender, unprompted
    public const byte ButtonPressed = 0xF9;
    public const byte Error = 0xA5;

    /// <summary>
    /// Record that carries the LED configuration at the end of the <see cref="GetAllParameters"/> dump.
    /// GameLink skips this byte, so the real value is unconfirmed; 0xD0 (SET_LED_STRIP_COLOR) is a guess.
    /// </summary>
    public const byte LedConfig = 0xD0;

    /// <summary>First byte of a binary LED datagram on the UDP motion port.</summary>
    public const byte UdpLedPrefix = 0xB2;

    // Catalogued only: no typed API until their behaviour is confirmed.
    public const byte ResetPorts = 0x01;
    public const byte SetSimuInputPort = 0x10;
    public const byte SetGameInputPort = 0x11;
    public const byte SetOutputPort = 0x12;
    public const byte SetName = 0x50;
    public const byte SetFan = 0x60;
    public const byte SetYawReachLimit = 0x63;
    public const byte SetYawLimitSpeed = 0x71;
    public const byte SetGameMode = 0x80;
    public const byte GetGameParams = 0x81;
    public const byte SetYawPid = 0x99;
    public const byte SetPitchPid = 0x9A;
    public const byte SetRollPid = 0x9B;
    public const byte SetGameIpAddress = 0xA4;
    public const byte SetLedStripMode = 0xB1;
    public const byte TcsSetting = 0xD5;
    public const byte LicenseSetup = 0xD6;
    public const byte ParkingSetting = 0xD7;
    public const byte GetPidParams = 0xF1;
    public const byte GetModes = 0xF2;
    public const byte GetModesAnswer = 0xF3;
    public const byte NewPidParams = 0xF5;
    public const byte DeleteGameMode = 0xF7;
    public const byte SetPidParams = 0xFF;

    private static readonly Dictionary<byte, string> Names = typeof(CommandIds)
        .GetFields()
        .Where(f => f.IsLiteral && f.FieldType == typeof(byte) && f.Name != nameof(UdpLedPrefix))
        .GroupBy(f => (byte)f.GetRawConstantValue()!)
        .ToDictionary(g => g.Key, g => string.Join("/", g.Select(f => f.Name)));

    /// <summary>Readable name for logging, e.g. <c>CheckIn</c> or <c>Unknown(0x5A)</c>.</summary>
    public static string NameOf(byte id) => Names.TryGetValue(id, out var name) ? name : $"Unknown(0x{id:X2})";
}
