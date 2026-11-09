using System.Collections.Concurrent;
using System.Net;
using YawVR.Link.Emulator;
using YawVR.Link.Sender;

namespace YawVR.Link.Tests;

/// <summary>A YawSender driving a YawEmulator over localhost, on random ports.</summary>
public class LoopbackTests : IAsyncLifetime
{
    private readonly TestEmulator emulator = new();
    private readonly List<TestSender> senders = new();

    public Task InitializeAsync() => emulator.StartAsync();

    public async Task DisposeAsync()
    {
        foreach (var s in senders) await s.DisposeAsync();
        await emulator.DisposeAsync();
    }

    private TestSender NewSender(string game = "TestGame")
    {
        var options = new SenderOptions
        {
            GameName = game,
            TelemetryPort = 0,
            DiscoveryReplyPort = 0,
            DeviceUdpPort = emulator.UdpPort,
            RequestTimeout = TimeSpan.FromSeconds(2),
        };
        options.DiscoveryTargets.Add(IPAddress.Loopback);
        var sender = new TestSender(options);
        senders.Add(sender);
        return sender;
    }

    private async Task<TestSender> ConnectedSender(string game = "TestGame")
    {
        var sender = NewSender(game);
        var device = (await sender.DiscoverAsync(TimeSpan.FromMilliseconds(700))).Single();
        await sender.StartAsync(device);
        return sender;
    }

    private static async Task Eventually(Func<bool> condition, string what)
    {
        var until = DateTime.UtcNow.AddSeconds(3);
        while (!condition())
        {
            if (DateTime.UtcNow > until) throw new TimeoutException("Timed out waiting for " + what);
            await Task.Delay(20);
        }
    }

    [Fact]
    public async Task Discovery_finds_the_emulator()
    {
        var sender = NewSender();
        var devices = await sender.DiscoverAsync(TimeSpan.FromMilliseconds(700));
        var device = Assert.Single(devices);
        Assert.Equal("Test Chair", device.Name);
        Assert.Equal(YawModel.Yaw2, device.Model);
        Assert.Equal(emulator.TcpPort, device.TcpPort);
        Assert.False(device.Reserved);
    }

    [Fact]
    public async Task Check_in_reserves_the_device_and_reads_its_settings()
    {
        var sender = await ConnectedSender();

        Assert.True(sender.CheckIn!.Available);
        Assert.Equal(DeviceEdition.Pro, sender.CheckIn.Edition);
        Assert.Equal(EmulatorState.Ready, emulator.State);
        Assert.Equal("TestGame", emulator.Owner!.GameName);
        Assert.Equal(new TiltLimits(15, 55, 20), sender.Parameters.TiltLimits);
        await Eventually(() => sender.Parameters.LedConfig.Length == 37, "LED config record");
        Assert.Equal(7, sender.SystemInfo!.RpiVersion);
    }

    [Fact]
    public async Task Second_game_is_told_the_device_is_reserved()
    {
        await ConnectedSender("First");
        var second = NewSender("Second");
        var ex = await Assert.ThrowsAsync<DeviceReservedException>(() => second.StartAsync(IPAddress.Loopback, YawModel.Yaw2, emulator.TcpPort));
        Assert.Equal("First", ex.Result.ReservedBy);
    }

    [Fact]
    public async Task Settings_reach_the_device_and_echo_back()
    {
        var sender = await ConnectedSender();
        await sender.SetPowerAsync(42);
        await sender.SetTiltLimitsAsync(new TiltLimits(PitchForward: 10, PitchBackward: 25, Roll: 12));

        await Eventually(() => sender.Parameters.Power == 42 && sender.Parameters.TiltLimits.PitchBackward == 25, "echoes");
        Assert.Equal(42, emulator.Parameters.Power);
        Assert.Equal(new TiltLimits(10, 25, 12), emulator.Parameters.TiltLimits);
    }

    [Fact]
    public async Task Emulator_can_refuse_a_setting()
    {
        var sender = await ConnectedSender();
        await sender.SetYawLimitAsync(720);
        await Eventually(() => sender.Parameters.YawLimit == 360, "clamped yaw limit echo");
    }

    [Fact]
    public async Task Motion_flows_to_the_emulator_and_telemetry_back()
    {
        var sender = await ConnectedSender();
        await sender.StartMotionAsync();
        sender.SetPose(10, -5, 3);

        await Eventually(() => emulator.Motion.Count > 3, "motion frames");
        Assert.Equal(EmulatorState.Running, emulator.State);
        Assert.True(emulator.Started);
        Assert.Equal(new Orientation(10, -5, 3), emulator.LastCommandedPose);

        await Eventually(() => sender.LastTelemetry?.Actual == new Orientation(10, -5, 3), "telemetry");
        Assert.Equal(12.6f, sender.LastTelemetry!.BatteryVoltage!.Value, precision: 2);
    }

    [Fact]
    public async Task Queries_return_device_values()
    {
        var sender = await ConnectedSender();
        var status = await sender.GetStatusAsync();
        Assert.Equal(DeviceStateKind.Disabled, status.State);

        await sender.StartMotionAsync();
        await Eventually(() => emulator.Started, "start");
        Assert.Equal(DeviceStateKind.SimulationMode, (await sender.GetStatusAsync()).State);

        Assert.Equal(new byte[] { 41, 42, 43 }, (await sender.GetTemperaturesAsync()).Values);

        var offset = await sender.SetSimOffsetAsync(new Orientation(1, 2, 3));
        Assert.Equal(new Orientation(1, 2, 3), offset);
        Assert.Equal(new Orientation(1, 2, 3), await sender.GetSimOffsetAsync());
    }

    [Fact]
    public async Task Arcade_buttons_reach_the_sender()
    {
        var sender = await ConnectedSender();
        await emulator.PressAsync(ArcadeEvent.GreenPressed);
        await Eventually(() => sender.Buttons.Contains(ArcadeEvent.GreenPressed), "button event");
    }

    [Fact]
    public async Task Stop_with_park_reaches_the_emulator()
    {
        var sender = await ConnectedSender();
        await sender.StartMotionAsync();
        await sender.StopMotionAsync(park: true);
        await Eventually(() => emulator.StoppedWithPark == true, "park stop");
        Assert.Equal(EmulatorState.Ready, emulator.State);
    }

    [Fact]
    public async Task Exit_releases_the_device()
    {
        var sender = await ConnectedSender();
        await sender.StopAsync();
        await Eventually(() => emulator.State == EmulatorState.Available && emulator.Owner is null, "release");

        var next = await ConnectedSender("Next");
        Assert.True(next.CheckIn!.Available);
    }

    [Fact]
    public async Task Emulator_shutdown_disconnects_the_sender()
    {
        var sender = await ConnectedSender();
        await emulator.StopAsync();
        await Eventually(() => sender.Disconnects.Count > 0, "disconnect callback");
    }

    private sealed class TestEmulator : YawEmulator
    {
        public TestEmulator() : base(new EmulatorOptions { UdpPort = 0, TcpPort = 0, BindAddress = IPAddress.Loopback }) { }

        public override DeviceInfo DeviceInfo { get; } =
            new(YawModel.Yaw2, "TEST01", "Test Chair") { Edition = DeviceEdition.Pro, RpiVersion = 7 };

        public ConcurrentQueue<MotionFrame> Motion { get; } = new();
        public volatile bool Started;
        public bool? StoppedWithPark;

        public Task PressAsync(ArcadeEvent evt) => SendArcadeButtonAsync(evt);

        protected override void OnStart() => Started = true;
        protected override void OnStop(bool park) { Started = false; StoppedWithPark = park; }
        protected override void OnMotion(MotionFrame frame) => Motion.Enqueue(frame);
        protected override int OnSetYawLimit(int degrees) => Math.Min(degrees, 360);
        protected override Temperatures OnGetTemperatures() => new(new byte[] { 41, 42, 43 });
        protected override float? GetBatteryVoltage() => 12.6f;
    }

    private sealed class TestSender : YawSender
    {
        public TestSender(SenderOptions options) : base(options) { }

        public ConcurrentBag<ArcadeEvent> Buttons { get; } = new();
        public ConcurrentBag<DisconnectReason> Disconnects { get; } = new();

        protected override void OnArcadeButton(ArcadeEvent evt) => Buttons.Add(evt);
        protected override void OnDisconnected(DisconnectReason reason) => Disconnects.Add(reason);
    }
}
