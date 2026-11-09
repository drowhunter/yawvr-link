# YawVR.Link

A .NET 8 library for building programs that behave like a YAW motion simulator (`YawEmulator`) or drive one (`YawSender`), without touching sockets, byte codes or the ASCII formats. The protocol comes from the recovered GameLink and Unity emulator sources in this repository; see the [plan](https://claude.ai/artifact/XE9gMvxY3EdsCFQGUvFv2r) for the full inventory and the reasoning.

## Layout

| Path | What it is |
| --- | --- |
| `src/YawVR.Link/Emulator` | `YawEmulator` base class, options, `SenderSession` |
| `src/YawVR.Link/Sender` | `YawSender` base class, options, discovery types |
| `src/YawVR.Link/Protocol` | Command IDs, `Packets` encoders, `TcpFramer`, UDP `TextFormats` (public, for tools and debugging) |
| `src/YawVR.Link/Models.cs` | Shared request, response and value types |
| `tests/YawVR.Link.Tests` | Golden-byte, framer and sender-against-emulator loopback tests |
| `samples/EmulatorConsole` | A console device that prints what it receives |
| `samples/SenderConsole` | Finds a device, checks in and sweeps yaw for 10 s |

## Build and test

```
dotnet build
dotnet test
dotnet run --project samples/EmulatorConsole -- yaw2 "My Chair"
dotnet run --project samples/SenderConsole -- 127.0.0.1
```

## Emulator

```csharp
class ChairEmulator : YawEmulator
{
    readonly MyChair chair = new();
    public override DeviceInfo DeviceInfo { get; } = new(YawModel.Yaw2, "CHAIR01", "My Motion Chair");

    protected override void OnStart() => chair.Enable();
    protected override void OnStop(bool park) => chair.Disable(park);
    protected override void OnMotion(MotionFrame f) => chair.MoveTo(f.Pose.Yaw, f.Pose.Pitch, f.Pose.Roll);
    protected override Orientation GetActualPose() => chair.ReadEncoders();
}

await using var emulator = new ChairEmulator();
await emulator.StartAsync();
```

Adapters can override `OnStarting()` to initialize `Parameters` and reset device
state before sockets open, and `OnStopped()` to clean up after shutdown. These
hooks run for each start/stop cycle; callers keep using `StartAsync()` and
`StopAsync()`. A failed socket bind releases partially opened listeners so startup
can be retried.

The base class answers discovery, owns the reservation (one checked-in game at a time), sends every reply and echo, and streams telemetry at 100 Hz. Settings callbacks return the value the device accepts, so `protected override int OnSetYawLimit(int d) => Math.Min(d, 360);` clamps and echoes the clamped value. If the owning game disconnects while running, the base class calls `OnStop(false)` so the chair does not keep moving.

## Sender

```csharp
await using var sender = new YawSender(new() { GameName = "MyGame" });
var device = (await sender.DiscoverAsync(TimeSpan.FromSeconds(2))).First();
await sender.StartAsync(device);          // check-in, system info, parameters
await sender.StartMotionAsync();
sender.SetPose(yaw, pitch, roll);         // each frame; the motion loop sends at 50 Hz
```

Queries (`GetStatusAsync`, `GetTemperaturesAsync`, `GetSystemInfoAsync`, `GetParametersAsync`, `GetSimOffsetAsync`) await the matching reply. Settings (`SetPowerAsync`, `SetTiltLimitsAsync`, ...) are fire-and-forget like GameLink: the echo updates `Parameters` and fires `OnParametersChanged`. Unprompted messages (arcade buttons, errors) arrive through callbacks.

## Default ports

| Port | Use |
| --- | --- |
| UDP 50010 | Device: discovery queries and motion |
| TCP 50020 | Device: control |
| UDP 50050 | Sender: discovery replies |
| UDP 50011 | Sender: telemetry |

All are configurable; tests run on random ports.

## Still unconfirmed against a real device

These are best guesses from the source and should be checked against a capture from `capture/yaw_capture.py`:

- Field 1 of the check-in answer (the emulator sends its device ID).
- The byte after `E5` in the state reply (sent as total length) and state fields 1 and 2 (sent as `0`).
- The ID byte of the LED-config record at the end of the parameter dump (`0xD0` is a guess).
- Temperature reply length (three bytes, as GameLink reads).
- Whether the device echoes STOP with or without the park byte.
- The source port real devices send telemetry from.
