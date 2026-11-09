using System.Net;
using System.Net.Sockets;
using YawVR.Link.Emulator;

namespace YawVR.Link.Tests;

public class EmulatorLifecycleTests
{
    [Fact]
    public async Task Lifecycle_hooks_initialize_each_start_and_finish_each_stop()
    {
        await using var emulator = new LifecycleEmulator(new() { UdpPort = 0, TcpPort = 0 });
        await emulator.StartAsync();
        Assert.Equal(1, emulator.Starts);
        Assert.Equal(87, emulator.Parameters.Power);
        Assert.True(emulator.IsRunning);
        await emulator.StopAsync();
        Assert.Equal(1, emulator.Stops);
        Assert.False(emulator.IsRunning);
        await emulator.StartAsync();
        Assert.Equal(2, emulator.Starts);
        Assert.Equal(87, emulator.Parameters.Power);
        await emulator.StopAsync();
        Assert.Equal(2, emulator.Stops);
    }

    [Fact]
    public async Task Failed_tcp_bind_releases_udp_and_allows_retry()
    {
        var occupied = new TcpListener(IPAddress.Loopback, 0);
        occupied.Start();
        var tcpPort = ((IPEndPoint)occupied.LocalEndpoint).Port;
        using var freeUdp = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        var udpPort = ((IPEndPoint)freeUdp.Client.LocalEndPoint!).Port;
        freeUdp.Dispose();
        await using var emulator = new LifecycleEmulator(new()
        {
            BindAddress = IPAddress.Loopback, TcpPort = tcpPort, UdpPort = udpPort
        });
        try
        {
            await Assert.ThrowsAsync<SocketException>(() => emulator.StartAsync());
            Assert.False(emulator.IsRunning);
            using var rebound = new UdpClient(new IPEndPoint(IPAddress.Loopback, udpPort));
        }
        finally { occupied.Stop(); }
        await emulator.StartAsync();
        Assert.True(emulator.IsRunning);
    }

    private sealed class LifecycleEmulator(EmulatorOptions options) : YawEmulator(options)
    {
        public override DeviceInfo DeviceInfo { get; } = new(YawModel.Yaw2, "TEST", "Lifecycle");
        public int Starts { get; private set; }
        public int Stops { get; private set; }
        protected override void OnStarting()
        {
            Assert.False(IsRunning);
            Starts++;
            Parameters.Power = 87;
        }
        protected override void OnStopped()
        {
            Assert.False(IsRunning);
            Stops++;
        }
    }
}
