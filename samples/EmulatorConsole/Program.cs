using YawVR.Link;
using YawVR.Link.Emulator;

// A console "device": GameLink, SimHub or the SenderConsole sample can find and drive it.
// Usage: EmulatorConsole [yaw1|yaw2|yaw3] [name]
var model = args.Length > 0 && Enum.TryParse<YawModel>(args[0], true, out var m) ? m : YawModel.Yaw2;
var name = args.Length > 1 ? args[1] : "Console Emulator";

await using var emulator = new ConsoleEmulator(new DeviceInfo(model, "CONSOLE01", name));
await emulator.StartAsync();
Console.WriteLine($"{model} '{name}' listening on UDP {emulator.UdpPort}, TCP {emulator.TcpPort}.");
if (Console.IsInputRedirected)
{
    // No keyboard (service, pipe): run until Ctrl+C.
    var quit = new TaskCompletionSource();
    Console.CancelKeyPress += (_, e) => { e.Cancel = true; quit.TrySetResult(); };
    Console.WriteLine("Press Ctrl+C to quit.");
    await quit.Task;
    return;
}

Console.WriteLine("Keys: G/R arcade buttons, I get in, O get out, Q quit.");

while (true)
{
    var key = Console.ReadKey(intercept: true).Key;
    if (key == ConsoleKey.Q) break;
    var evt = key switch
    {
        ConsoleKey.G => ArcadeEvent.GreenPressed,
        ConsoleKey.R => ArcadeEvent.RedPressed,
        ConsoleKey.I => ArcadeEvent.GetIn,
        ConsoleKey.O => ArcadeEvent.GetOut,
        _ => (ArcadeEvent?)null,
    };
    if (evt is { } e) await emulator.PressAsync(e);
}

internal sealed class ConsoleEmulator : YawEmulator
{
    private DateTime lastPrint;

    public ConsoleEmulator(DeviceInfo info) => DeviceInfo = info;

    public override DeviceInfo DeviceInfo { get; }

    public Task PressAsync(ArcadeEvent evt)
    {
        Console.WriteLine($"Sending {evt}");
        return SendArcadeButtonAsync(evt);
    }

    protected override CheckInResponse OnCheckIn(CheckInRequest request)
    {
        var response = base.OnCheckIn(request);
        Console.WriteLine($"Check-in from '{request.GameName}' at {request.Remote}: {(response.Available ? "accepted" : "reserved")}");
        return response;
    }

    protected override void OnAppCheckIn(AppCheckInRequest request) => Console.WriteLine($"App check-in from {request.Remote}");
    protected override void OnSenderDisconnected(SenderSession session, DisconnectReason reason) => Console.WriteLine($"{session} disconnected ({reason})");
    protected override void OnStart() => Console.WriteLine("START");
    protected override void OnStop(bool park) => Console.WriteLine($"STOP{(park ? " and park" : "")}");
    protected override void OnExit(SenderSession session) => Console.WriteLine($"EXIT from {session}");
    protected override void OnCalibrate(bool allAxes) => Console.WriteLine($"CALIBRATE all axes: {allAxes}");
    protected override void OnSaveSettings(DeviceParameters p) => Console.WriteLine($"SAVE power {p.Power}, tilt {p.TiltLimits}, yaw limit {p.YawLimit}");

    protected override int OnSetPower(int percent) { Console.WriteLine($"Power {percent}"); return percent; }
    protected override TiltLimits OnSetTiltLimits(TiltLimits limits) { Console.WriteLine($"Tilt {limits}"); return limits; }
    protected override int OnSetYawLimit(int degrees) { Console.WriteLine($"Yaw limit {degrees}"); return degrees; }

    protected override void OnMotion(MotionFrame frame)
    {
        if (DateTime.UtcNow - lastPrint < TimeSpan.FromMilliseconds(250)) return;
        lastPrint = DateTime.UtcNow;
        Console.WriteLine($"Motion {frame.Pose}{(frame.Vibration is { } v ? $" vib {v.Amplitude0}@{v.FrequencyHz}Hz" : "")}");
    }

    protected override void OnLeds(LedFrame frame) => Console.WriteLine($"LEDs: {frame.Colors.Count} colours");
    protected override void OnUnknownCommand(SenderSession session, byte id, ReadOnlyMemory<byte> record) =>
        Console.WriteLine($"Unhandled command 0x{id:X2}: {Convert.ToHexString(record.Span)}");
}
