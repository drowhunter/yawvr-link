using System.Collections.Concurrent;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using YawVR.Link.Protocol;

namespace YawVR.Link.Sender;

/// <summary>
/// Drives a YAW simulator (real or emulated) the way GameLink does: discovery, check-in, commands,
/// motion streaming and telemetry. Use it directly, or derive from it and override the callbacks you need.
/// </summary>
public class YawSender : IAsyncDisposable
{
    private static readonly TimeSpan DiscoveryInterval = TimeSpan.FromMilliseconds(500);
    private static readonly TimeSpan DeviceTimeout = TimeSpan.FromSeconds(2);

    private readonly SenderOptions options;
    private readonly ILogger log;
    private readonly CallbackInvoker invoke;
    private readonly object gate = new();

    // Discovery
    private readonly ConcurrentDictionary<IPAddress, DiscoveredDevice> found = new();
    private UdpClient? discoveryUdp;
    private CancellationTokenSource? discoveryCts;
    private Task? discoveryTask;

    // Connection
    private TcpClient? tcp;
    private NetworkStream? stream;
    private TcpFramer? framer;
    private UdpClient? telemetryUdp;
    private UdpClient? motionUdp;
    private IPEndPoint? motionEndpoint;
    private CancellationTokenSource? connectionCts;
    private readonly List<Task> connectionLoops = new();
    private readonly SemaphoreSlim writeLock = new(1, 1);
    private readonly ConcurrentDictionary<byte, TaskCompletionSource<TcpRecord>> pending = new();
    private readonly ConcurrentDictionary<byte, SemaphoreSlim> requestLocks = new();
    private TaskCompletionSource<DeviceParameters>? dumpCompletion;
    private DateTime expectLedConfigUntil;
    private bool dirty;
    private int disconnectSignalled;

    // Motion state
    private Orientation pose;
    private Vibration? vibration;
    private byte? fan;
    private volatile bool streaming;

    public YawSender(SenderOptions? options = null)
    {
        this.options = options ?? new SenderOptions();
        log = (this.options.LoggerFactory ?? NullLoggerFactory.Instance).CreateLogger(GetType());
        invoke = new CallbackInvoker(this.options.CallbackContext, log);
    }

    // ---------------------------------------------------------------- state

    public DiscoveredDevice? Device { get; private set; }
    public YawModel Model => Device?.Model ?? YawModel.Yaw2;
    public bool IsConnected => tcp?.Connected == true;
    public bool IsStreaming => streaming;
    public CheckInResult? CheckIn { get; private set; }
    public DeviceParameters Parameters { get { lock (gate) return parameters.Clone(); } }
    public DeviceStatus? Status { get; private set; }
    public Temperatures? Temperatures { get; private set; }
    public SystemInfo? SystemInfo { get; private set; }
    public Telemetry? LastTelemetry { get; private set; }
    public IReadOnlyCollection<DiscoveredDevice> FoundDevices => found.Values.ToArray();

    /// <summary>Local telemetry port actually bound (useful when <see cref="SenderOptions.TelemetryPort"/> is 0).</summary>
    public int TelemetryPort => (telemetryUdp?.Client.LocalEndPoint as IPEndPoint)?.Port ?? options.TelemetryPort;

    private DeviceParameters parameters = new();

    // ---------------------------------------------------------------- callbacks

    protected virtual void OnDeviceFound(DiscoveredDevice device) { }
    protected virtual void OnDeviceLost(DiscoveredDevice device) { }
    protected virtual void OnCheckIn(CheckInResult result) { }
    protected virtual void OnStatusChanged(DeviceStatus status) { }
    protected virtual void OnParametersChanged(DeviceParameters parameters) { }
    protected virtual void OnTemperatures(Temperatures temperatures) { }
    protected virtual void OnSystemInfo(SystemInfo info) { }

    /// <summary>A telemetry datagram arrived (about 100 Hz).</summary>
    protected virtual void OnTelemetry(Telemetry telemetry) { }

    protected virtual void OnArcadeButton(ArcadeEvent evt) { }
    protected virtual void OnDeviceError(string message) { }
    protected virtual void OnDisconnected(DisconnectReason reason) { }
    protected virtual void OnUnknownMessage(byte id, ReadOnlyMemory<byte> record) { }

    // ---------------------------------------------------------------- discovery

    /// <summary>Discovers for <paramref name="duration"/> and returns every device that answered.</summary>
    public async Task<IReadOnlyList<DiscoveredDevice>> DiscoverAsync(TimeSpan duration, CancellationToken ct = default)
    {
        bool startedHere = discoveryTask is null;
        if (startedHere) StartDiscovery();
        try { await Task.Delay(duration, ct).ConfigureAwait(false); }
        finally { if (startedHere) await StopDiscoveryAsync().ConfigureAwait(false); }
        return found.Values.OrderBy(d => d.Name).ToArray();
    }

    /// <summary>Starts continuous discovery: queries every 500 ms, <see cref="OnDeviceFound"/> and <see cref="OnDeviceLost"/> fire as devices come and go.</summary>
    public void StartDiscovery()
    {
        if (discoveryTask is not null) return;
        found.Clear();
        discoveryUdp = new UdpClient(AddressFamily.InterNetwork) { EnableBroadcast = true };
        discoveryUdp.Client.Bind(new IPEndPoint(IPAddress.Any, options.DiscoveryReplyPort));
        discoveryCts = new CancellationTokenSource();
        var ct = discoveryCts.Token;
        discoveryTask = Task.WhenAll(Task.Run(() => DiscoverySendLoopAsync(ct), ct), Task.Run(() => DiscoveryReceiveLoopAsync(ct), ct));
    }

    public async Task StopDiscoveryAsync()
    {
        if (discoveryTask is null) return;
        discoveryCts!.Cancel();
        discoveryUdp?.Dispose();
        try { await discoveryTask.ConfigureAwait(false); } catch (OperationCanceledException) { }
        discoveryCts.Dispose();
        discoveryTask = null;
        discoveryUdp = null;
    }

    public void StopDiscovery() => StopDiscoveryAsync().GetAwaiter().GetResult();

    private async Task DiscoverySendLoopAsync(CancellationToken ct)
    {
        var query = Encoding.ASCII.GetBytes(TextFormats.DiscoveryQuery);
        while (!ct.IsCancellationRequested)
        {
            foreach (var target in DiscoveryTargets())
            {
                try { await discoveryUdp!.SendAsync(query, new IPEndPoint(target, options.DeviceUdpPort), ct).ConfigureAwait(false); }
                catch (Exception ex) when (ex is SocketException or ObjectDisposedException) { }
            }

            foreach (var (ip, device) in found)
            {
                if (DateTimeOffset.UtcNow - device.LastSeen > DeviceTimeout && found.TryRemove(ip, out _))
                    invoke.Post(nameof(OnDeviceLost), () => OnDeviceLost(device));
            }

            try { await Task.Delay(DiscoveryInterval, ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { return; }
        }
    }

    private async Task DiscoveryReceiveLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            UdpReceiveResult result;
            try { result = await discoveryUdp!.ReceiveAsync(ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { return; }
            catch (ObjectDisposedException) { return; }
            catch (SocketException) { continue; }

            var reply = DiscoveryReply.Parse(Encoding.ASCII.GetString(result.Buffer));
            if (reply is null) continue;

            var address = result.RemoteEndPoint.Address;
            bool isNew = !found.ContainsKey(address);
            var device = new DiscoveredDevice(address, reply);
            found[address] = device;
            if (isNew) invoke.Post(nameof(OnDeviceFound), () => OnDeviceFound(device));
        }
    }

    private IEnumerable<IPAddress> DiscoveryTargets()
    {
        if (options.DiscoveryTargets.Count > 0) return options.DiscoveryTargets;

        var targets = new List<IPAddress>();
        foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (nic.OperationalStatus != OperationalStatus.Up || nic.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
            foreach (var u in nic.GetIPProperties().UnicastAddresses)
            {
                if (u.Address.AddressFamily != AddressFamily.InterNetwork || u.IPv4Mask is null) continue;
                var a = u.Address.GetAddressBytes();
                var m = u.IPv4Mask.GetAddressBytes();
                for (int i = 0; i < 4; i++) a[i] = (byte)(a[i] | ~m[i]);
                targets.Add(new IPAddress(a));
            }
        }
        if (targets.Count == 0) targets.Add(IPAddress.Broadcast);
        return targets;
    }

    // ---------------------------------------------------------------- connect and disconnect

    public Task<CheckInResult> StartAsync(DiscoveredDevice device, CancellationToken ct = default) =>
        ConnectAsync(device, ct);

    public Task<CheckInResult> StartAsync(IPAddress address, YawModel model, int tcpPort = 50020, CancellationToken ct = default) =>
        ConnectAsync(new DiscoveredDevice(address, new DiscoveryReply(model, "", address.ToString(), tcpPort, false)), ct);

    public CheckInResult Start(DiscoveredDevice device) => StartAsync(device).GetAwaiter().GetResult();

    /// <summary>
    /// GameLink's connect sequence: TCP connect, CHECK_IN with the telemetry port, then system info and
    /// parameters. Throws <see cref="DeviceReservedException"/> if another game owns the device.
    /// </summary>
    private async Task<CheckInResult> ConnectAsync(DiscoveredDevice device, CancellationToken ct)
    {
        if (IsConnected) throw new InvalidOperationException("Already connected. Call StopAsync first.");

        Device = device;
        lock (gate) parameters = DeviceParameters.DefaultsFor(device.Model);
        disconnectSignalled = 0;
        connectionCts = new CancellationTokenSource();
        var lifetime = connectionCts.Token;

        telemetryUdp = new UdpClient(AddressFamily.InterNetwork);
        telemetryUdp.Client.Bind(new IPEndPoint(IPAddress.Any, options.TelemetryPort));
        motionUdp = new UdpClient(AddressFamily.InterNetwork);
        motionEndpoint = new IPEndPoint(device.Address, options.DeviceUdpPort);

        tcp = new TcpClient(AddressFamily.InterNetwork) { NoDelay = true };
        try
        {
            await tcp.ConnectAsync(device.Address, device.TcpPort, ct).ConfigureAwait(false);
        }
        catch
        {
            await CleanupAsync().ConfigureAwait(false);
            throw;
        }
        stream = tcp.GetStream();
        framer = new TcpFramer(TcpDirection.DeviceToSender, () => Model);

        connectionLoops.Add(Task.Run(() => ReceiveLoopAsync(lifetime), lifetime));
        connectionLoops.Add(Task.Run(() => TelemetryLoopAsync(lifetime), lifetime));

        TcpRecord answer;
        try
        {
            answer = await RequestAsync(Packets.CheckIn(TelemetryPort, options.GameName), CommandIds.CheckInAnswer, ct).ConfigureAwait(false);
        }
        catch
        {
            await StopAsync().ConfigureAwait(false);
            throw;
        }

        var result = ParseCheckIn(answer);
        CheckIn = result;
        invoke.Post(nameof(OnCheckIn), () => OnCheckIn(result));
        if (!result.Available)
        {
            await CleanupAsync().ConfigureAwait(false);
            throw new DeviceReservedException(result);
        }

        if (options.ReadDeviceOnConnect)
        {
            try
            {
                await GetSystemInfoAsync(ct).ConfigureAwait(false);
                await GetParametersAsync(ct).ConfigureAwait(false);
            }
            catch (TimeoutException ex)
            {
                log.LogWarning(ex, "Device did not answer the startup queries");
            }
        }

        connectionLoops.Add(Task.Run(() => MotionLoopAsync(lifetime), lifetime));
        if (options.PollStatus) connectionLoops.Add(Task.Run(() => PollLoopAsync(lifetime), lifetime));
        streaming = options.StreamMotionOnConnect;
        return result;
    }

    /// <summary>Sends EXIT, closes the connection and stops streaming.</summary>
    public async Task StopAsync()
    {
        if (tcp is null) return;
        streaming = false;
        if (IsConnected)
        {
            try { await SendAsync(Packets.Exit(), CancellationToken.None).ConfigureAwait(false); }
            catch (Exception ex) when (ex is IOException or SocketException or ObjectDisposedException) { }
        }
        await CleanupAsync().ConfigureAwait(false);
        SignalDisconnect(DisconnectReason.LocalStop);
    }

    public void Stop() => StopAsync().GetAwaiter().GetResult();

    public async ValueTask DisposeAsync()
    {
        await StopAsync().ConfigureAwait(false);
        await StopDiscoveryAsync().ConfigureAwait(false);
        GC.SuppressFinalize(this);
    }

    private async Task CleanupAsync()
    {
        streaming = false;
        connectionCts?.Cancel();
        tcp?.Close();
        telemetryUdp?.Dispose();
        motionUdp?.Dispose();
        foreach (var p in pending.Values) p.TrySetCanceled();
        pending.Clear();
        dumpCompletion?.TrySetCanceled();

        var loops = connectionLoops.ToArray();
        connectionLoops.Clear();
        var current = Task.CurrentId;
        try { await Task.WhenAll(loops.Where(t => t.Id != current)).WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false); }
        catch (Exception) { /* loops end with cancellation */ }

        tcp = null;
        stream = null;
        telemetryUdp = null;
        motionUdp = null;
        connectionCts?.Dispose();
        connectionCts = null;
    }

    private void SignalDisconnect(DisconnectReason reason)
    {
        if (Interlocked.Exchange(ref disconnectSignalled, 1) == 1) return;
        invoke.Post(nameof(OnDisconnected), () => OnDisconnected(reason));
    }

    // ---------------------------------------------------------------- motion

    /// <summary>Sets the pose the motion loop sends. Call it as often as you like, e.g. every game frame.</summary>
    public void SetPose(float yaw, float pitch, float roll) => SetPose(new Orientation(yaw, pitch, roll));

    public void SetPose(Orientation value) { lock (gate) pose = value; }

    /// <summary>Adds a <c>V[..]</c> vibration suffix to motion datagrams; null removes it.</summary>
    public void SetVibration(Vibration? value) { lock (gate) vibration = value; }

    public void SetVibration(byte amplitude, byte frequencyHz) => SetVibration(new Vibration(amplitude, frequencyHz));

    /// <summary>Adds an <c>F[..]</c> fan or smart-plug suffix to motion datagrams; null removes it.</summary>
    public void SetFan(byte? value) { lock (gate) fan = value; }

    /// <summary>Sends one motion datagram now, outside the motion loop.</summary>
    public void SendPoseNow(Orientation value) => SendDatagram(Encoding.ASCII.GetBytes(TextFormats.Motion(value)));

    public void SendVibrationNow(Vibration value) => SendDatagram(Encoding.ASCII.GetBytes(TextFormats.VibrationOnly(value)));

    public void SetLedColor(YawColor color) => SendDatagram(Packets.LedColor(color));

    /// <summary>Sends a buffered LED frame. Header bytes are a counter on Yaw1 and unit/segment ids on Yaw2.</summary>
    public void SetLeds(ReadOnlySpan<YawColor> colors, byte header1 = 1, byte header2 = 1) =>
        SendDatagram(Packets.LedStrip(header1, header2, colors));

    private void SendDatagram(byte[] bytes)
    {
        var udp = motionUdp;
        var endpoint = motionEndpoint;
        if (udp is null || endpoint is null) throw new InvalidOperationException("Not connected.");
        try { udp.Send(bytes, bytes.Length, endpoint); }
        catch (SocketException ex) { log.LogDebug(ex, "Motion send failed"); }
    }

    private async Task MotionLoopAsync(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1.0 / Math.Max(1, options.MotionRateHz)));
        try
        {
            while (await timer.WaitForNextTickAsync(ct).ConfigureAwait(false))
            {
                if (!streaming) continue;
                string text;
                lock (gate) text = TextFormats.Motion(pose, vibration, fan);
                try { SendDatagram(Encoding.ASCII.GetBytes(text)); }
                catch (Exception ex) when (ex is InvalidOperationException or ObjectDisposedException) { return; }
            }
        }
        catch (OperationCanceledException) { }
    }

    // ---------------------------------------------------------------- control commands

    /// <summary>Sends START and begins streaming motion.</summary>
    public async Task StartMotionAsync(CancellationToken ct = default)
    {
        await SendAsync(Packets.Start(), ct).ConfigureAwait(false);
        streaming = true;
    }

    /// <summary>Stops streaming motion and sends STOP. <paramref name="park"/> asks the device to park.</summary>
    public async Task StopMotionAsync(bool park = false, CancellationToken ct = default)
    {
        streaming = false;
        await SendAsync(Packets.Stop(park), ct).ConfigureAwait(false);
    }

    public Task SetPowerAsync(int percent, CancellationToken ct = default) => SendSettingAsync(Packets.SetPower(percent), ct);
    public Task SetTiltLimitsAsync(TiltLimits limits, CancellationToken ct = default) => SendSettingAsync(Packets.SetTiltLimits(limits), ct);
    public Task SetYawLimitAsync(int degrees, CancellationToken ct = default) => SendSettingAsync(Packets.SetYawLimit(degrees), ct);
    public Task SetYawReturnLimitAsync(float value, CancellationToken ct = default) => SendSettingAsync(Packets.SetYawReturnLimit(value), ct);
    public Task SetHasYawLimitAsync(bool enabled, CancellationToken ct = default) => SendSettingAsync(Packets.SetHasYawLimit(enabled), ct);

    public Task SetVibrationEnabledAsync(bool enabled, byte limit = 0, CancellationToken ct = default) =>
        SendSettingAsync(Packets.SetHasVibration(Model, enabled, limit), ct);

    public Task CalibrateAsync(bool allAxes, CancellationToken ct = default) => SendAsync(Packets.Calibrate(allAxes), ct);
    public Task SaveSettingsAsync(CancellationToken ct = default) => SendAsync(Packets.SaveSettings(), ct);
    public Task RebootAsync(CancellationToken ct = default) => SendAsync(Packets.Reboot(), ct);
    public Task SendLedEffectsAsync(ReadOnlyMemory<byte> data, CancellationToken ct = default) => SendAsync(Packets.LedEffects(data.Span), ct);

    /// <summary>
    /// Settings are fire-and-forget, like GameLink: the device's echo updates <see cref="Parameters"/> and
    /// fires <see cref="OnParametersChanged"/> when it arrives.
    /// </summary>
    private async Task SendSettingAsync(byte[] bytes, CancellationToken ct)
    {
        await SendAsync(bytes, ct).ConfigureAwait(false);
        dirty = true;
    }

    // ---------------------------------------------------------------- queries

    public async Task<DeviceStatus> GetStatusAsync(CancellationToken ct = default) =>
        ParseStatus(await RequestAsync(Packets.GetState(), CommandIds.GetState, ct).ConfigureAwait(false));

    public async Task<Temperatures> GetTemperaturesAsync(CancellationToken ct = default) =>
        ParseTemperatures(await RequestAsync(Packets.GetTemperatures(), CommandIds.GetTemperatures, ct).ConfigureAwait(false));

    public async Task<SystemInfo> GetSystemInfoAsync(CancellationToken ct = default) =>
        new(SystemInfoText(await RequestAsync(Packets.GetSystemInfo(), CommandIds.SystemInfo, ct).ConfigureAwait(false)));

    public async Task<Orientation> GetSimOffsetAsync(CancellationToken ct = default) =>
        ParseOffset(await RequestAsync(Packets.SimOffset(false, Orientation.Zero), CommandIds.SimOffset, ct).ConfigureAwait(false));

    public async Task<Orientation> SetSimOffsetAsync(Orientation offset, CancellationToken ct = default) =>
        ParseOffset(await RequestAsync(Packets.SimOffset(true, offset), CommandIds.SimOffset, ct).ConfigureAwait(false));

    /// <summary>Asks for every setting (GET_ALL_PARAMETERS) and waits for the whole dump.</summary>
    public async Task<DeviceParameters> GetParametersAsync(CancellationToken ct = default)
    {
        var gateLock = requestLocks.GetOrAdd(CommandIds.GetAllParameters, _ => new SemaphoreSlim(1, 1));
        await gateLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var completion = new TaskCompletionSource<DeviceParameters>(TaskCreationOptions.RunContinuationsAsynchronously);
            dumpCompletion = completion;
            framer!.InParameterDump = true;
            await SendAsync(Packets.GetAllParameters(), ct).ConfigureAwait(false);
            try
            {
                return await completion.Task.WaitAsync(options.RequestTimeout, ct).ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                throw new TimeoutException("No complete parameter dump from the device.");
            }
            finally
            {
                if (framer is not null) framer.InParameterDump = false;
                dumpCompletion = null;
            }
        }
        finally { gateLock.Release(); }
    }

    // ---------------------------------------------------------------- TCP plumbing

    private async Task SendAsync(byte[] bytes, CancellationToken ct)
    {
        var s = stream ?? throw new InvalidOperationException("Not connected.");
        await writeLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            log.LogTrace("-> {Record}", new TcpRecord(bytes));
            await s.WriteAsync(bytes, ct).ConfigureAwait(false);
        }
        finally { writeLock.Release(); }
    }

    /// <summary>Sends a request and waits for the reply with <paramref name="replyId"/>. One request per reply ID at a time.</summary>
    private async Task<TcpRecord> RequestAsync(byte[] request, byte replyId, CancellationToken ct)
    {
        var gateLock = requestLocks.GetOrAdd(replyId, _ => new SemaphoreSlim(1, 1));
        await gateLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var completion = new TaskCompletionSource<TcpRecord>(TaskCreationOptions.RunContinuationsAsynchronously);
            pending[replyId] = completion;
            await SendAsync(request, ct).ConfigureAwait(false);
            try
            {
                return await completion.Task.WaitAsync(options.RequestTimeout, ct).ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                throw new TimeoutException($"No {CommandIds.NameOf(replyId)} reply within {options.RequestTimeout.TotalMilliseconds} ms.");
            }
            finally { pending.TryRemove(replyId, out _); }
        }
        finally { gateLock.Release(); }
    }

    private async Task ReceiveLoopAsync(CancellationToken ct)
    {
        var buffer = new byte[2048];
        var reason = DisconnectReason.RemoteClosed;
        try
        {
            while (!ct.IsCancellationRequested)
            {
                int n = await stream!.ReadAsync(buffer, ct).ConfigureAwait(false);
                if (n == 0) break;
                framer!.Append(buffer.AsSpan(0, n));
                foreach (var record in framer.ReadAll()) Dispatch(record);
            }
        }
        catch (OperationCanceledException) { return; }
        catch (Exception ex) when (ex is IOException or SocketException or ObjectDisposedException)
        {
            if (ct.IsCancellationRequested) return;
            reason = DisconnectReason.Error;
        }

        if (ct.IsCancellationRequested) return;
        log.LogInformation("Device closed the connection ({Reason})", reason);
        _ = CleanupAsync();
        SignalDisconnect(reason);
    }

    private void Dispatch(TcpRecord r)
    {
        log.LogTrace("<- {Record}", r);
        bool parameterRecord = false;

        switch (r.Id)
        {
            case CommandIds.SetPower when r.Length >= 5:
                lock (gate) parameters.Power = r.ReadInt32(1);
                parameterRecord = true;
                break;
            case CommandIds.SetTiltLimits when r.Length >= 13:
                lock (gate) parameters.TiltLimits = new TiltLimits(PitchForward: r.ReadInt32(5), PitchBackward: r.ReadInt32(1), Roll: r.ReadInt32(9));
                parameterRecord = true;
                break;
            case CommandIds.SetYawLimit when r.Length >= 5:
                lock (gate) parameters.YawLimit = r.ReadInt32(1);
                parameterRecord = true;
                break;
            case CommandIds.SetYawReturnLimit when r.Length >= 5:
                lock (gate) parameters.YawReturnLimit = r.ReadSingle(1);
                parameterRecord = true;
                break;
            case CommandIds.SetHasYawLimit when r.Length >= 2:
                lock (gate) parameters.HasYawLimit = r.ReadBool(1);
                parameterRecord = true;
                break;
            case CommandIds.SetHasVibration when r.Length >= 2:
                lock (gate)
                {
                    parameters.HasVibration = r.ReadBool(1);
                    if (r.Length >= 3) parameters.VibrationLimit = r.Bytes[2];
                }
                parameterRecord = true;
                if (framer!.InParameterDump)
                {
                    // Last setting in the dump. Yaw1 and Yaw2 follow it with an LED configuration record.
                    framer.InParameterDump = false;
                    if (Model != YawModel.Yaw3) expectLedConfigUntil = DateTime.UtcNow.AddMilliseconds(500);
                    CompleteDump();
                    return;
                }
                break;

            case CommandIds.GetState:
            {
                var status = ParseStatus(r);
                var previous = Status;
                Status = status;
                if (previous is null || previous.State != status.State || previous.Occupied != status.Occupied || previous.Calibrating != status.Calibrating)
                    invoke.Post(nameof(OnStatusChanged), () => OnStatusChanged(status));
                break;
            }
            case CommandIds.GetTemperatures:
            {
                var t = ParseTemperatures(r);
                Temperatures = t;
                invoke.Post(nameof(OnTemperatures), () => OnTemperatures(t));
                break;
            }
            case CommandIds.SystemInfo:
            {
                var info = new SystemInfo(SystemInfoText(r));
                SystemInfo = info;
                invoke.Post(nameof(OnSystemInfo), () => OnSystemInfo(info));
                break;
            }
            case CommandIds.ButtonPressed when r.Length >= 2:
            {
                var evt = (ArcadeEvent)r.Bytes[1];
                invoke.Post(nameof(OnArcadeButton), () => OnArcadeButton(evt));
                break;
            }
            case CommandIds.Error:
            {
                var message = r.ReadAscii(1);
                invoke.Post(nameof(OnDeviceError), () => OnDeviceError(message));
                break;
            }
            case CommandIds.CheckInAnswer or CommandIds.SimOffset or CommandIds.Start or CommandIds.Stop or CommandIds.Exit:
                break;

            default:
                if (DateTime.UtcNow < expectLedConfigUntil)
                {
                    expectLedConfigUntil = default;
                    lock (gate) parameters.LedConfig = r.Bytes.AsSpan(1).ToArray();
                    var snapshot = Parameters;
                    invoke.Post(nameof(OnParametersChanged), () => OnParametersChanged(snapshot));
                    return;
                }
                var data = r.Bytes.AsMemory();
                invoke.Post(nameof(OnUnknownMessage), () => OnUnknownMessage(r.Id, data));
                break;
        }

        if (pending.TryRemove(r.Id, out var waiter)) waiter.TrySetResult(r);

        if (parameterRecord && !framer!.InParameterDump)
        {
            var snapshot = Parameters;
            invoke.Post(nameof(OnParametersChanged), () => OnParametersChanged(snapshot));
        }
    }

    private void CompleteDump()
    {
        var snapshot = Parameters;
        dumpCompletion?.TrySetResult(snapshot);
        // The LED record may follow; OnParametersChanged fires again when it arrives.
        invoke.Post(nameof(OnParametersChanged), () => OnParametersChanged(snapshot));
    }

    private async Task PollLoopAsync(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
        try
        {
            while (await timer.WaitForNextTickAsync(ct).ConfigureAwait(false))
            {
                try
                {
                    await SendAsync(Packets.GetState(), ct).ConfigureAwait(false);
                    await SendAsync(Packets.GetTemperatures(), ct).ConfigureAwait(false);
                    if (options.AutoSave && dirty)
                    {
                        dirty = false;
                        await Task.Delay(10, ct).ConfigureAwait(false);
                        await SendAsync(Packets.SaveSettings(), ct).ConfigureAwait(false);
                    }
                }
                catch (Exception ex) when (ex is IOException or SocketException or ObjectDisposedException or InvalidOperationException) { return; }
            }
        }
        catch (OperationCanceledException) { }
    }

    // ---------------------------------------------------------------- UDP telemetry

    private async Task TelemetryLoopAsync(CancellationToken ct)
    {
        var udp = telemetryUdp!;
        while (!ct.IsCancellationRequested)
        {
            UdpReceiveResult result;
            try { result = await udp.ReceiveAsync(ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { return; }
            catch (ObjectDisposedException) { return; }
            catch (SocketException) { continue; }

            // Like GameLink: only the selected device's address counts.
            if (Device is { } d && !result.RemoteEndPoint.Address.Equals(d.Address)) continue;
            var telemetry = TextFormats.ParseTelemetry(Encoding.ASCII.GetString(result.Buffer));
            if (telemetry is null) continue;
            LastTelemetry = telemetry;
            invoke.Post(nameof(OnTelemetry), () => OnTelemetry(telemetry));
        }
    }

    // ---------------------------------------------------------------- parsing

    private static CheckInResult ParseCheckIn(TcpRecord r)
    {
        var text = r.ReadAscii(1).TrimEnd('\0');
        var f = text.Split(';');
        bool available = f[0] == "AVAILABLE";
        var edition = DeviceEdition.Standard;
        if (available && f.Length >= 3) Enum.TryParse(f[2], ignoreCase: true, out edition);
        return new CheckInResult(available, edition, text)
        {
            ReservedBy = available ? null : f.ElementAtOrDefault(1),
            ReservedIp = available ? null : f.ElementAtOrDefault(2),
        };
    }

    private static DeviceStatus ParseStatus(TcpRecord r)
    {
        var text = r.ReadAscii(2).Trim('\0', ' ', '\r', '\n');
        var f = text.Split(';');
        return new DeviceStatus(
            DeviceStatus.FromWire(f[0]),
            Occupied: f.Length >= 4 && f[3] != "0",
            Calibrating: f.Length >= 5 && f[4] != "0") { Raw = text };
    }

    private static Temperatures ParseTemperatures(TcpRecord r) => new(r.Bytes.Skip(1).ToArray());

    private static string SystemInfoText(TcpRecord r) => r.ReadAscii(1).TrimEnd('\0');

    private static Orientation ParseOffset(TcpRecord r) =>
        r.Length >= 13 ? new Orientation(r.ReadSingle(1), r.ReadSingle(5), r.ReadSingle(9)) : Orientation.Zero;
}
