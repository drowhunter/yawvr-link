using System.Net;
using YawVR.Link;
using YawVR.Link.Sender;

// Finds a device (real or emulated), checks in and sweeps the yaw axis gently for 10 seconds.
// Usage: SenderConsole [device-ip]   (without an IP it broadcasts discovery)
var options = new SenderOptions { GameName = "SenderConsole" };
if (args.Length > 0) options.DiscoveryTargets.Add(IPAddress.Parse(args[0]));

await using var sender = new ConsoleSender(options);
Console.WriteLine("Discovering for 2 s...");
var devices = await sender.DiscoverAsync(TimeSpan.FromSeconds(2));
if (devices.Count == 0)
{
    Console.WriteLine("No device answered.");
    return;
}

var device = devices[0];
Console.WriteLine($"Connecting to {device}");
var checkIn = await sender.StartAsync(device);
Console.WriteLine($"Checked in: {checkIn.Raw}");
Console.WriteLine($"Settings: power {sender.Parameters.Power}, tilt {sender.Parameters.TiltLimits}, yaw limit {sender.Parameters.YawLimit}");
Console.WriteLine($"Status: {(await sender.GetStatusAsync()).State}");

await sender.StartMotionAsync();
var start = DateTime.UtcNow;
while (DateTime.UtcNow - start < TimeSpan.FromSeconds(10))
{
    double t = (DateTime.UtcNow - start).TotalSeconds;
    sender.SetPose((float)(20 * Math.Sin(t)), 0, 0);
    await Task.Delay(20);
}
sender.SetPose(0, 0, 0);
await Task.Delay(500);
await sender.StopMotionAsync();
await sender.StopAsync();
Console.WriteLine("Done.");

internal sealed class ConsoleSender : YawSender
{
    private DateTime lastPrint;

    public ConsoleSender(SenderOptions options) : base(options) { }

    protected override void OnTelemetry(Telemetry telemetry)
    {
        if (DateTime.UtcNow - lastPrint < TimeSpan.FromMilliseconds(500)) return;
        lastPrint = DateTime.UtcNow;
        Console.WriteLine($"Telemetry {telemetry.Actual}");
    }

    protected override void OnStatusChanged(DeviceStatus status) => Console.WriteLine($"Status changed: {status.State}");
    protected override void OnArcadeButton(ArcadeEvent evt) => Console.WriteLine($"Button: {evt}");
    protected override void OnDisconnected(DisconnectReason reason) => Console.WriteLine($"Disconnected ({reason})");
    protected override void OnUnknownMessage(byte id, ReadOnlyMemory<byte> record) =>
        Console.WriteLine($"Unknown message 0x{id:X2}: {Convert.ToHexString(record.Span)}");
}
