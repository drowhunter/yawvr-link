using System.Net;
using System.Net.Sockets;
using System.Text;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using YawVR.Link.Protocol;

namespace YawVR.Link.Emulator;

/// <summary>
/// Base class for a program that behaves like a YAW simulator. It answers discovery, accepts TCP control
/// connections, receives UDP motion and sends telemetry. Override <see cref="DeviceInfo"/> and the
/// callbacks your device needs; the base class sends every reply and echo.
/// </summary>
public abstract class YawEmulator : IAsyncDisposable
{
    private readonly EmulatorOptions options;
    private readonly ILogger log;
    private readonly CallbackInvoker invoke;
    private readonly object gate = new();
    private readonly List<SenderSession> sessions = new();
    private readonly List<Task> loops = new();

    private UdpClient? udp;
    private TcpListener? listener;
    private CancellationTokenSource? cts;
    private SenderSession? owner;
    private EmulatorState state;
    private Orientation lastCommanded;
    private Orientation simOffset;

    protected YawEmulator(EmulatorOptions? options = null)
    {
        this.options = options ?? new EmulatorOptions();
        log = (this.options.LoggerFactory ?? NullLoggerFactory.Instance).CreateLogger(GetType());
        invoke = new CallbackInvoker(this.options.CallbackContext, log);
    }

    // ---------------------------------------------------------------- identity and state

    /// <summary>What this device says it is in discovery, check-in and system info.</summary>
    public abstract DeviceInfo DeviceInfo { get; }

    /// <summary>Current settings. Changed by SET commands from senders; change them locally and call <see cref="NotifyParametersChangedAsync"/>.</summary>
    public DeviceParameters Parameters { get; private set; } = new();

    public EmulatorState State { get { lock (gate) return state; } }

    /// <summary>The checked-in game, if any.</summary>
    public SenderSession? Owner { get { lock (gate) return owner; } }

    public IReadOnlyList<SenderSession> Sessions { get { lock (gate) return sessions.ToArray(); } }

    /// <summary>The last pose received over UDP.</summary>
    public Orientation LastCommandedPose { get { lock (gate) return lastCommanded; } }

    public bool IsRunning => cts is { IsCancellationRequested: false };

    /// <summary>Bound UDP port (useful when <see cref="EmulatorOptions.UdpPort"/> is 0).</summary>
    public int UdpPort => (udp?.Client.LocalEndPoint as IPEndPoint)?.Port ?? options.UdpPort;

    /// <summary>Bound TCP port (useful when <see cref="EmulatorOptions.TcpPort"/> is 0).</summary>
    public int TcpPort => (listener?.LocalEndpoint as IPEndPoint)?.Port ?? options.TcpPort;

    // ---------------------------------------------------------------- lifecycle

    public Task StartAsync(CancellationToken cancellationToken = default)
    {
        if (IsRunning) throw new InvalidOperationException("Emulator is already running.");

        Parameters = DeviceParameters.DefaultsFor(DeviceInfo.Model);
        lock (gate)
        {
            state = EmulatorState.Available;
            lastCommanded = Orientation.Zero;
            simOffset = Orientation.Zero;
        }
        OnStarting();
        cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

        try
        {
            udp = new UdpClient(AddressFamily.InterNetwork) { EnableBroadcast = true };
            udp.Client.Bind(new IPEndPoint(options.BindAddress, options.UdpPort));

            listener = new TcpListener(options.BindAddress, options.TcpPort);
            listener.Start();
        }
        catch
        {
            listener?.Stop();
            udp?.Dispose();
            cts.Dispose();
            listener = null;
            udp = null;
            cts = null;
            throw;
        }

        var ct = cts.Token;
        loops.Add(Task.Run(() => UdpLoopAsync(ct), ct));
        loops.Add(Task.Run(() => AcceptLoopAsync(ct), ct));
        loops.Add(Task.Run(() => TelemetryLoopAsync(ct), ct));

        log.LogInformation("Emulating {Model} '{Name}' on UDP {Udp}, TCP {Tcp}", DeviceInfo.Model, DeviceInfo.Name, UdpPort, TcpPort);
        return Task.CompletedTask;
    }

    public async Task StopAsync()
    {
        if (cts is null) return;
        cts.Cancel();
        listener?.Stop();
        udp?.Dispose();

        SenderSession[] open;
        lock (gate) { open = sessions.ToArray(); }
        foreach (var s in open) s.Close();

        try { await Task.WhenAll(loops).ConfigureAwait(false); }
        catch (OperationCanceledException) { }
        loops.Clear();

        foreach (var s in open) EndSession(s, DisconnectReason.LocalStop);
        cts.Dispose();
        cts = null;
        udp = null;
        listener = null;
        OnStopped();
    }

    public void Start() => StartAsync().GetAwaiter().GetResult();
    public void Stop() => StopAsync().GetAwaiter().GetResult();

    public async ValueTask DisposeAsync()
    {
        await StopAsync().ConfigureAwait(false);
        GC.SuppressFinalize(this);
    }

    // ---------------------------------------------------------------- callbacks: lifecycle

    /// <summary>
    /// Called on the caller's thread after default parameters are created, before sockets are opened.
    /// Initialize device settings and reset adapter state here. Called on every start, including restarts.
    /// </summary>
    protected virtual void OnStarting() { }

    /// <summary>Called on shutdown after listeners, telemetry and sender sessions have stopped.</summary>
    protected virtual void OnStopped() { }

    // ---------------------------------------------------------------- callbacks: discovery and sessions

    /// <summary>Answer to a <c>YAW_CALLING</c> query. Return null to stay hidden.</summary>
    protected virtual DiscoveryReply? OnDiscovery(IPEndPoint from) =>
        new(DeviceInfo.Model, DeviceInfo.Id, DeviceInfo.Name, TcpPort, Owner is not null);

    /// <summary>Decide whether a game may check in. The default accepts unless another game owns the device.</summary>
    protected virtual CheckInResponse OnCheckIn(CheckInRequest request)
    {
        var current = Owner;
        return current is null || current.Remote.Equals(request.Remote)
            ? CheckInResponse.Accept()
            : CheckInResponse.Reserved(current.GameName ?? "", current.Remote.Address.ToString());
    }

    /// <summary>Called after the Yaw app checks in. App sessions never own the device.</summary>
    protected virtual void OnAppCheckIn(AppCheckInRequest request) { }

    protected virtual void OnSenderConnected(SenderSession session) { }
    protected virtual void OnSenderDisconnected(SenderSession session, DisconnectReason reason) { }

    // ---------------------------------------------------------------- callbacks: control

    protected virtual void OnStart() { }
    protected virtual void OnStop(bool park) { }

    /// <summary>The owning game sent EXIT and released the device.</summary>
    protected virtual void OnExit(SenderSession session) { }

    protected virtual void OnCalibrate(bool allAxes) { }
    protected virtual void OnReboot() { }
    protected virtual void OnSaveSettings(DeviceParameters parameters) { }
    protected virtual void OnDebugRegister(bool register, bool debugMode) { }
    protected virtual void OnLedEffects(ReadOnlyMemory<byte> data) { }

    // ---------------------------------------------------------------- callbacks: settings
    // Each returns the value the device accepts; it is stored in Parameters and echoed back.

    protected virtual int OnSetPower(int percent) => percent;
    protected virtual TiltLimits OnSetTiltLimits(TiltLimits limits) => limits;
    protected virtual int OnSetYawLimit(int degrees) => degrees;
    protected virtual float OnSetYawReturnLimit(float value) => value;
    protected virtual bool OnSetHasYawLimit(bool enabled) => enabled;
    protected virtual (bool Enabled, byte Limit) OnSetVibration(bool enabled, byte limit) => (enabled, limit);

    // ---------------------------------------------------------------- callbacks: queries

    protected virtual DeviceStatus OnGetStatus() =>
        new(State == EmulatorState.Running ? DeviceStateKind.SimulationMode : DeviceStateKind.Disabled);

    protected virtual Temperatures OnGetTemperatures() => new(new byte[] { 30, 30, 30 });

    protected virtual SystemInfo OnGetSystemInfo()
    {
        var text = "VirtualHere:" + (DeviceInfo.VirtualHereActive ? "active" : "inactive");
        if (DeviceInfo.RpiVersion is { } v) text += "\nRPIVER:" + v;
        return new SystemInfo(text);
    }

    /// <summary>SIM_OFFSET: <paramref name="set"/> true stores <paramref name="offset"/>. Returns the current offset.</summary>
    protected virtual Orientation OnSimOffset(bool set, Orientation offset)
    {
        if (set) simOffset = offset;
        return simOffset;
    }

    // ---------------------------------------------------------------- callbacks: real time

    /// <summary>A motion datagram arrived. Runs at the sender's rate (about 50 Hz for GameLink).</summary>
    protected virtual void OnMotion(MotionFrame frame) { }

    protected virtual void OnLeds(LedFrame frame) { }

    /// <summary>Pose reported in telemetry. Defaults to the last commanded pose; override to report real sensors.</summary>
    protected virtual Orientation GetActualPose() => LastCommandedPose;

    /// <summary>Battery voltage for the telemetry <c>U[..]</c> field, or null to omit it.</summary>
    protected virtual float? GetBatteryVoltage() => null;

    protected virtual void OnUnknownCommand(SenderSession session, byte id, ReadOnlyMemory<byte> record) { }

    protected virtual void OnUnknownDatagram(IPEndPoint from, ReadOnlyMemory<byte> data) { }

    // ---------------------------------------------------------------- device-side commands

    /// <summary>Sends an arcade button or seat event to every connected sender.</summary>
    protected Task SendArcadeButtonAsync(ArcadeEvent evt) => BroadcastAsync(Packets.ButtonPressed(evt));

    protected Task SendErrorAsync(string message) => BroadcastAsync(Packets.Error(message));

    /// <summary>Pushes every setting as individual echo records, e.g. after a change on the device itself.</summary>
    protected async Task NotifyParametersChangedAsync()
    {
        var p = Parameters;
        var model = DeviceInfo.Model;
        foreach (var record in new[]
                 {
                     Packets.SetPower(p.Power), Packets.SetTiltLimits(p.TiltLimits), Packets.SetYawLimit(p.YawLimit),
                     Packets.SetHasYawLimit(p.HasYawLimit), Packets.SetYawReturnLimit(p.YawReturnLimit),
                     Packets.SetHasVibration(model, p.HasVibration, p.VibrationLimit),
                 })
            await BroadcastAsync(record).ConfigureAwait(false);
    }

    private async Task BroadcastAsync(byte[] bytes)
    {
        foreach (var s in Sessions)
        {
            try { await s.SendAsync(bytes, cts?.Token ?? default).ConfigureAwait(false); }
            catch (Exception ex) { log.LogDebug(ex, "Send to {Session} failed", s); }
        }
    }

    // ---------------------------------------------------------------- UDP: discovery and motion

    private async Task UdpLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            UdpReceiveResult result;
            try { result = await udp!.ReceiveAsync(ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { return; }
            catch (ObjectDisposedException) { return; }
            catch (SocketException ex) { log.LogDebug(ex, "UDP receive error"); continue; }

            try { HandleDatagram(result.Buffer, result.RemoteEndPoint); }
            catch (Exception ex) { log.LogWarning(ex, "Bad datagram from {From}", result.RemoteEndPoint); }
        }
    }

    private void HandleDatagram(byte[] data, IPEndPoint from)
    {
        if (data.Length == 0) return;

        if (data[0] == CommandIds.UdpLedPrefix)
        {
            invoke.Post(nameof(OnLeds), () => OnLeds(new LedFrame(data, from)));
            return;
        }

        var text = Encoding.ASCII.GetString(data);
        if (text.Contains(TextFormats.DiscoveryQuery, StringComparison.Ordinal))
        {
            var reply = invoke.Call(nameof(OnDiscovery), () => OnDiscovery(from), null);
            if (reply is not null)
            {
                var bytes = Encoding.ASCII.GetBytes(reply.ToWire());
                udp?.Send(bytes, bytes.Length, from);
            }
            return;
        }

        var frame = TextFormats.ParseMotion(text, from);
        if (frame is null)
        {
            invoke.Post(nameof(OnUnknownDatagram), () => OnUnknownDatagram(from, data));
            return;
        }

        if (options.MotionRequiresStart && State != EmulatorState.Running) return;
        if (frame.HasPose) lock (gate) lastCommanded = frame.Pose;
        invoke.Post(nameof(OnMotion), () => OnMotion(frame));
    }

    // ---------------------------------------------------------------- UDP: telemetry

    private async Task TelemetryLoopAsync(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1.0 / Math.Max(1, options.TelemetryRateHz)));
        try
        {
            while (await timer.WaitForNextTickAsync(ct).ConfigureAwait(false))
            {
                var targets = Sessions.Where(s => s.TelemetryEndpoint is not null).ToArray();
                if (targets.Length == 0) continue;

                var actual = invoke.Call(nameof(GetActualPose), GetActualPose, LastCommandedPose);
                var battery = invoke.Call(nameof(GetBatteryVoltage), GetBatteryVoltage, null);
                foreach (var s in targets)
                {
                    var text = s.IsApp ? TextFormats.AppTelemetry(actual, LastCommandedPose) : TextFormats.Telemetry(actual, battery);
                    var bytes = Encoding.ASCII.GetBytes(text);
                    try { udp?.Send(bytes, bytes.Length, s.TelemetryEndpoint!); }
                    catch (Exception ex) when (ex is SocketException or ObjectDisposedException) { }
                }
            }
        }
        catch (OperationCanceledException) { }
    }

    // ---------------------------------------------------------------- TCP

    private async Task AcceptLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            TcpClient client;
            try { client = await listener!.AcceptTcpClientAsync(ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { return; }
            catch (Exception ex) when (ex is ObjectDisposedException or SocketException) { return; }

            client.NoDelay = true;
            var session = new SenderSession(client, () => DeviceInfo.Model);
            lock (gate) sessions.Add(session);
            log.LogInformation("Sender connected: {Remote}", session.Remote);
            invoke.Post(nameof(OnSenderConnected), () => OnSenderConnected(session));
            _ = Task.Run(() => SessionLoopAsync(session, ct), ct);
        }
    }

    private async Task SessionLoopAsync(SenderSession session, CancellationToken ct)
    {
        var buffer = new byte[2048];
        var reason = DisconnectReason.RemoteClosed;
        try
        {
            while (!ct.IsCancellationRequested)
            {
                int n = await session.Stream.ReadAsync(buffer, ct).ConfigureAwait(false);
                if (n == 0) break;
                session.Framer.Append(buffer.AsSpan(0, n));
                foreach (var record in session.Framer.ReadAll())
                {
                    if (await HandleRecordAsync(session, record, ct).ConfigureAwait(false))
                    {
                        reason = DisconnectReason.Exit;
                    }
                }
            }
        }
        catch (OperationCanceledException) { reason = DisconnectReason.LocalStop; }
        catch (Exception ex) when (ex is IOException or SocketException or ObjectDisposedException)
        {
            reason = ct.IsCancellationRequested ? DisconnectReason.LocalStop : DisconnectReason.Error;
        }

        if (!ct.IsCancellationRequested)
        {
            session.Close();
            EndSession(session, reason);
        }
    }

    private void EndSession(SenderSession session, DisconnectReason reason)
    {
        bool wasOwner, wasRunning;
        lock (gate)
        {
            if (!sessions.Remove(session)) return;
            wasOwner = owner == session;
            wasRunning = wasOwner && state == EmulatorState.Running;
            if (wasOwner)
            {
                owner = null;
                state = EmulatorState.Available;
            }
        }
        log.LogInformation("Sender disconnected: {Session} ({Reason})", session, reason);
        // A real chair should not keep moving when its game vanishes.
        if (wasRunning) invoke.Call(nameof(OnStop), () => OnStop(false));
        invoke.Post(nameof(OnSenderDisconnected), () => OnSenderDisconnected(session, reason));
    }

    /// <summary>Handles one record. Returns true when the session sent EXIT.</summary>
    private async Task<bool> HandleRecordAsync(SenderSession session, TcpRecord r, CancellationToken ct)
    {
        log.LogTrace("<- {Session}: {Record}", session, r);
        byte[]? reply = null;
        bool exited = false;
        var model = DeviceInfo.Model;

        if (IsControlCommand(r.Id) && options.RequireOwnerForControl && Owner != session)
        {
            log.LogDebug("Ignoring {Record} from non-owner {Session}", r, session);
            return false;
        }

        switch (r.Id)
        {
            case CommandIds.CheckIn when r.Length >= 5:
            {
                var request = new CheckInRequest(r.ReadAscii(5), r.ReadInt32(1), session.Remote);
                var response = invoke.Call(nameof(OnCheckIn), () => OnCheckIn(request), CheckInResponse.Reserved("", ""));
                if (response.Available)
                {
                    lock (gate)
                    {
                        owner = session;
                        if (state == EmulatorState.Available) state = EmulatorState.Ready;
                    }
                    session.GameName = request.GameName;
                    session.TelemetryEndpoint = new IPEndPoint(session.Remote.Address, request.TelemetryPort);
                }
                reply = Packets.CheckInAnswer(response, DeviceInfo);
                break;
            }

            case CommandIds.CheckInApp when r.Length >= 5:
            {
                var request = new AppCheckInRequest(r.ReadInt32(1), session.Remote);
                session.IsApp = true;
                session.TelemetryEndpoint = new IPEndPoint(session.Remote.Address, request.TelemetryPort);
                invoke.Call(nameof(OnAppCheckIn), () => OnAppCheckIn(request));
                var current = Owner;
                reply = Packets.CheckInAppAnswer(current is not null, State == EmulatorState.Running, current?.GameName ?? "", model);
                break;
            }

            case CommandIds.Start:
                lock (gate) state = EmulatorState.Running;
                invoke.Call(nameof(OnStart), OnStart);
                reply = Packets.Ack(CommandIds.Start);
                break;

            case CommandIds.Stop:
            {
                bool park = r.ReadBool(1);
                lock (gate) state = owner is null ? EmulatorState.Available : EmulatorState.Ready;
                invoke.Call(nameof(OnStop), () => OnStop(park));
                reply = Packets.Ack(CommandIds.Stop);
                break;
            }

            case CommandIds.Exit:
                if (Owner == session)
                {
                    bool wasRunning;
                    lock (gate)
                    {
                        wasRunning = state == EmulatorState.Running;
                        owner = null;
                        state = EmulatorState.Available;
                    }
                    session.TelemetryEndpoint = null;
                    if (wasRunning) invoke.Call(nameof(OnStop), () => OnStop(false));
                    invoke.Call(nameof(OnExit), () => OnExit(session));
                    reply = Packets.Ack(CommandIds.Exit);
                    exited = true;
                }
                break;

            case CommandIds.SetPower when r.Length >= 5:
                Parameters.Power = Math.Clamp(invoke.Call(nameof(OnSetPower), () => OnSetPower(r.ReadInt32(1)), Parameters.Power), 0, 99);
                reply = Packets.SetPower(Parameters.Power);
                break;

            case CommandIds.SetTiltLimits when r.Length >= 13:
            {
                var requested = new TiltLimits(PitchForward: r.ReadInt32(5), PitchBackward: r.ReadInt32(1), Roll: r.ReadInt32(9));
                Parameters.TiltLimits = invoke.Call(nameof(OnSetTiltLimits), () => OnSetTiltLimits(requested), Parameters.TiltLimits);
                reply = Packets.SetTiltLimits(Parameters.TiltLimits);
                break;
            }

            case CommandIds.SetYawLimit when r.Length >= 5:
                Parameters.YawLimit = invoke.Call(nameof(OnSetYawLimit), () => OnSetYawLimit(r.ReadInt32(1)), Parameters.YawLimit);
                reply = Packets.SetYawLimit(Parameters.YawLimit);
                break;

            case CommandIds.SetYawReturnLimit when r.Length >= 5:
                Parameters.YawReturnLimit = invoke.Call(nameof(OnSetYawReturnLimit), () => OnSetYawReturnLimit(r.ReadSingle(1)), Parameters.YawReturnLimit);
                reply = Packets.SetYawReturnLimit(Parameters.YawReturnLimit);
                break;

            case CommandIds.SetHasYawLimit when r.Length >= 2:
                Parameters.HasYawLimit = invoke.Call(nameof(OnSetHasYawLimit), () => OnSetHasYawLimit(r.ReadBool(1)), Parameters.HasYawLimit);
                reply = Packets.SetHasYawLimit(Parameters.HasYawLimit);
                break;

            case CommandIds.SetHasVibration when r.Length >= 2:
            {
                byte limit = r.Length >= 3 ? r.Bytes[2] : Parameters.VibrationLimit;
                var (enabled, accepted) = invoke.Call(nameof(OnSetVibration), () => OnSetVibration(r.ReadBool(1), limit),
                    (Parameters.HasVibration, Parameters.VibrationLimit));
                Parameters.HasVibration = enabled;
                Parameters.VibrationLimit = accepted;
                reply = Packets.SetHasVibration(model, enabled, accepted);
                break;
            }

            case CommandIds.Calibrate:
                invoke.Call(nameof(OnCalibrate), () => OnCalibrate(r.ReadBool(1)));
                break;

            case CommandIds.SaveSettings:
                invoke.Call(nameof(OnSaveSettings), () => OnSaveSettings(Parameters.Clone()));
                break;

            case CommandIds.Reboot:
                invoke.Call(nameof(OnReboot), OnReboot);
                break;

            case CommandIds.DebugRegister:
                invoke.Call(nameof(OnDebugRegister), () => OnDebugRegister(r.ReadBool(1), r.ReadBool(2)));
                break;

            case CommandIds.LedEffects:
            {
                var data = r.Bytes.AsMemory(1);
                invoke.Post(nameof(OnLedEffects), () => OnLedEffects(data));
                break;
            }

            case CommandIds.GetAllParameters:
                reply = Packets.ParameterDump(Parameters, model);
                break;

            case CommandIds.GetState:
                reply = Packets.StateAnswer(invoke.Call(nameof(OnGetStatus), OnGetStatus, new DeviceStatus(DeviceStateKind.Disabled)));
                break;

            case CommandIds.GetTemperatures:
                reply = Packets.TemperaturesAnswer(invoke.Call(nameof(OnGetTemperatures), OnGetTemperatures, new Temperatures(new byte[3])));
                break;

            case CommandIds.SystemInfo:
                reply = Packets.SystemInfoAnswer(invoke.Call(nameof(OnGetSystemInfo), OnGetSystemInfo, new SystemInfo("")));
                break;

            case CommandIds.SimOffset when r.Length >= 14:
            {
                var offset = new Orientation(r.ReadSingle(2), r.ReadSingle(6), r.ReadSingle(10));
                reply = Packets.SimOffsetAnswer(invoke.Call(nameof(OnSimOffset), () => OnSimOffset(r.ReadBool(1), offset), simOffset));
                break;
            }

            default:
            {
                var data = r.Bytes.AsMemory();
                log.LogDebug("Unhandled {Record} from {Session}", r, session);
                invoke.Post(nameof(OnUnknownCommand), () => OnUnknownCommand(session, r.Id, data));
                break;
            }
        }

        if (reply is not null)
        {
            log.LogTrace("-> {Session}: {Reply}", session, Convert.ToHexString(reply));
            await session.SendAsync(reply, ct).ConfigureAwait(false);
        }
        return exited;
    }

    private static bool IsControlCommand(byte id) => id is CommandIds.Start or CommandIds.Stop or CommandIds.SetPower
        or CommandIds.SetTiltLimits or CommandIds.SetYawLimit or CommandIds.SetYawReturnLimit or CommandIds.SetHasYawLimit
        or CommandIds.SetHasVibration or CommandIds.Calibrate or CommandIds.Reboot or CommandIds.SaveSettings;
}
