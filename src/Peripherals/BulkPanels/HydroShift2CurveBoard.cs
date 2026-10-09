using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using Nexus.Service.Devices;
using Nexus.Service.Devices.Detection;
using Nexus.Service.Persistence;
using Nexus.Service.Platform;

namespace Nexus.Service.Peripherals.BulkPanels;

/// <summary>Where the head is believed to be, where it was asked to go, and whether it is moving.</summary>
public sealed record HydroShift2CurveHead(
    int Tilt, int Slide, int TargetTilt, int TargetSlide, bool Moving, bool Calibrating);

/// <summary>
/// The HydroShift II OLED Curved's second USB function (0416:8051): edge LEDs, pump, coolant
/// probe and the two head motors. Gated with the glass under one Nexus Control toggle. Every
/// command runs on this loop, so writes never interleave.
/// </summary>
public sealed class HydroShift2CurveBoard : BackgroundService
{
    private const int TickMs = 33;
    private const int ConnectRetryMs = 5000;
    private const int GateCheckMs = 2000;
    private const int StatusPollMs = 1000;

    /// <summary>L-Connect's resend cadence; the pump also holds a target on its own (measured).</summary>
    private const int PumpResendMs = 2000;

    private const int MotorPollMs = 100;
    private const int ReplyTimeoutMs = 250;
    private const byte WritePipe = 0x01;
    private const byte ReadPipe = 0x81;

    private static readonly int[] ProductIds = { HydroShift2CurveProtocol.BoardProductId };
    private static readonly byte[] Motors = { HydroShift2CurveProtocol.TiltMotor, HydroShift2CurveProtocol.SlideMotor };

    private readonly IBulkUsbPipeFactory _pipes;
    private readonly DeviceControlGate _gate;
    private readonly HardwarePresence _presence;
    private readonly IConfigStore _store;
    private readonly byte[] _reply = new byte[1024];

    private readonly object _lock = new();
    private IBulkUsbPipe? _pipe;
    private volatile bool _connected;
    private int? _coolantC;
    private int? _pumpRpm;
    private int? _pumpDuty;
    private bool _pumpDirty;
    private volatile bool _pumpDriven;
    private bool? _followsHeader;
    private bool _restoreHeaderFollow;
    private bool? _loggedFollow;
    private byte[]? _leds;
    private bool _ledsDirty;
    private int _tilt;
    private int _slide;
    private int _targetTilt;
    private int _targetSlide;
    private bool _calibrateRequested;
    private int _homingStep = -1;
    private readonly bool[] _motorBusy = new bool[3];

    private volatile TaskCompletionSource? _shutdown;

    private long _lastStatusAt;
    private long _lastPumpAt;
    private long _lastMotorPollAt;

    public HydroShift2CurveBoard(IBulkUsbPipeFactory pipes, DeviceControlGate gate, HardwarePresence presence, IConfigStore store)
    {
        _pipes = pipes;
        _gate = gate;
        _presence = presence;
        _store = store;
        var head = store.Load().Devices.HydroShift2Curve;
        _tilt = _targetTilt = Math.Clamp(head.Tilt, 0, HydroShift2CurveProtocol.TiltMax);
        _slide = _targetSlide = Math.Clamp(head.Slide, HydroShift2CurveProtocol.SlideMin, HydroShift2CurveProtocol.SlideMax);
        _calibrateRequested = head.Recalibrating;
    }

    public bool IsAvailable => _connected;

    /// <summary>Raised from the loop when <see cref="IsAvailable"/> flips.</summary>
    public event Action? AvailabilityChanged;

    public string? Firmware { get; private set; }

    public int? CoolantC { get { lock (_lock) { return _coolantC; } } }

    public int? PumpRpm { get { lock (_lock) { return _pumpRpm; } } }

    public int? PumpDuty { get { lock (_lock) { return _pumpDuty; } } }

    public HydroShift2CurveHead Head
    {
        get
        {
            lock (_lock)
            {
                var busy = _motorBusy[HydroShift2CurveProtocol.TiltMotor] || _motorBusy[HydroShift2CurveProtocol.SlideMotor];
                var calibrating = _calibrateRequested || _homingStep >= 0;
                return new HydroShift2CurveHead(_tilt, _slide, _targetTilt, _targetSlide,
                    busy || calibrating || _tilt != _targetTilt || _slide != _targetSlide, calibrating);
            }
        }
    }

    /// <summary>Drives the pump at a duty percent, or hands it back with null.</summary>
    public void SetPumpDuty(int? dutyPercent)
    {
        lock (_lock)
        {
            if (_pumpDuty == dutyPercent) return;
            _pumpDuty = dutyPercent;
            _pumpDirty = true;
        }
    }

    /// <summary>Sets the edge LEDs to one frame of packed RGB; the board holds it until the next.</summary>
    public void SetLeds(ReadOnlySpan<byte> rgb)
    {
        lock (_lock)
        {
            if (_leds is not null && rgb.SequenceEqual(_leds)) return;
            _leds = rgb.ToArray();
            _ledsDirty = true;
        }
    }

    /// <summary>Sets where the head should go; either axis may be left as it is with null.</summary>
    public void SetHeadTarget(int? tilt, int? slide)
    {
        lock (_lock)
        {
            if (tilt is { } t) _targetTilt = Math.Clamp(t, 0, HydroShift2CurveProtocol.TiltMax);
            if (slide is { } s) _targetSlide = Math.Clamp(s, HydroShift2CurveProtocol.SlideMin, HydroShift2CurveProtocol.SlideMax);
        }
    }

    /// <summary>Homes both motors against their end stops, then returns the head to its target.</summary>
    public void Recalibrate()
    {
        lock (_lock)
        {
            _calibrateRequested = true;
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            long lastGateAt = 0;
            while (!stoppingToken.IsCancellationRequested)
            {
                if (_shutdown is { } shutdown)
                {
                    // A glitch can have dropped the link with the pump still on Nexus's target.
                    if (_pumpDriven && !_connected)
                    {
                        TryConnect();
                    }
                    Disconnect(handBack: true);
                    shutdown.TrySetResult();
                    await Task.Delay(Timeout.Infinite, stoppingToken).ConfigureAwait(false);
                }
                var now = Environment.TickCount64;
                if (!_connected || now - lastGateAt >= GateCheckMs)
                {
                    lastGateAt = now;
                    if (!_gate.IsEnabled(HydroShift2CurveLcdDriver.Id)
                        || !_presence.UsbPresent(HydroShift2CurveProtocol.BoardVendorId, ProductIds))
                    {
                        Disconnect(handBack: true);
                        await Task.Delay(ConnectRetryMs, stoppingToken).ConfigureAwait(false);
                        continue;
                    }
                }
                if (!_connected && !TryConnect())
                {
                    await Task.Delay(ConnectRetryMs, stoppingToken).ConfigureAwait(false);
                    continue;
                }
                try
                {
                    Tick(Environment.TickCount64);
                }
                catch (Exception ex)
                {
                    ServiceLog.Warn($"[{HydroShift2CurveLcdDriver.Id}] board tick failed: {ex.Message}");
                }
                await Task.Delay(TickMs, stoppingToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            Disconnect(handBack: true);
        }
    }

    /// <summary>
    /// The fast shutdown skips this loop's own release, so it asks the loop to hand the pump back
    /// now (the board owns its pipe from that one thread) and waits for it, bounded.
    /// </summary>
    public void ReleaseForShutdown(TimeSpan timeout)
    {
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _shutdown = done;
        if (_connected || _pumpDriven)
        {
            done.Task.Wait(timeout);
        }
    }

    internal bool TryConnect()
    {
        var pipe = _pipes.Open(HydroShift2CurveProtocol.BoardVendorId, HydroShift2CurveProtocol.BoardProductId, WritePipe, ReadPipe);
        if (pipe is null)
        {
            return false;
        }
        _pipe = pipe;
        var version = Exchange(HydroShift2CurveProtocol.EncodeVersionQuery());
        if (version is null)
        {
            ServiceLog.Warn($"[{HydroShift2CurveLcdDriver.Id}] board did not answer its version query");
            pipe.Dispose();
            _pipe = null;
            return false;
        }
        Firmware = HydroShift2CurveProtocol.DecodeVersion(version);
        lock (_lock)
        {
            _ledsDirty = _leds is not null;
            _pumpDirty = _pumpDuty is not null;
            Array.Clear(_motorBusy);
        }
        _lastStatusAt = 0;
        _lastPumpAt = 0;
        _connected = true;
        ServiceLog.Info($"[{HydroShift2CurveLcdDriver.Id}] board connected, firmware {Firmware ?? "unknown"}");
        RaiseAvailabilityChanged();
        return true;
    }

    private void Disconnect(bool handBack)
    {
        if (_pipe is null)
        {
            return;
        }
        // A glitch disconnect keeps the hand-back owed: the pump still holds Nexus's target.
        if (handBack && _pumpDriven && HandBackPump())
        {
            _pumpDriven = false;
        }
        _pipe.Dispose();
        _pipe = null;
        lock (_lock)
        {
            _coolantC = null;
            _pumpRpm = null;
            _followsHeader = null;
            _homingStep = _homingStep >= 0 ? 0 : -1;
            Array.Clear(_motorBusy);
        }
        if (_connected)
        {
            _connected = false;
            ServiceLog.Info($"[{HydroShift2CurveLcdDriver.Id}] board disconnected");
            RaiseAvailabilityChanged();
        }
    }

    internal void Tick(long now)
    {
        if (now - _lastStatusAt >= StatusPollMs)
        {
            _lastStatusAt = now;
            var status = Exchange(HydroShift2CurveProtocol.EncodeQuery(HydroShift2CurveProtocol.BoardStatus));
            var speed = Exchange(HydroShift2CurveProtocol.EncodeQuery(HydroShift2CurveProtocol.BoardPumpRpm));
            if (status is null && speed is null)
            {
                Disconnect(handBack: false);
                return;
            }
            bool? reported = status is null ? null : HydroShift2CurveProtocol.DecodeFollowsHeader(status);
            if (reported is { } follows && _loggedFollow != follows)
            {
                _loggedFollow = follows;
                ServiceLog.Info($"[{HydroShift2CurveLcdDriver.Id}] pump {(follows ? "follows the motherboard header" : "runs its own speed")}");
            }
            lock (_lock)
            {
                _coolantC = status is null ? _coolantC : HydroShift2CurveProtocol.DecodeCoolant(status);
                _followsHeader = reported ?? _followsHeader;
                _pumpRpm = speed is null ? _pumpRpm : HydroShift2CurveProtocol.DecodePumpRpm(speed);
            }
            ApplyIdleFollow();
        }
        SendPumpIfDue(now);
        SendLedsIfDirty();
        TickMotors(now);
    }

    private void SendPumpIfDue(long now)
    {
        int? duty;
        bool? followsHeader;
        lock (_lock)
        {
            duty = _pumpDuty;
            followsHeader = _followsHeader;
            if (duty is null && !_pumpDriven) return;
            if (!_pumpDirty && now - _lastPumpAt < PumpResendMs) return;
            _pumpDirty = false;
        }
        _lastPumpAt = now;
        if (duty is null)
        {
            // A hand-back only counts once answered; until then it is retried on the resend cadence.
            if (HandBackPump())
            {
                _pumpDriven = false;
            }
            return;
        }
        // Following the motherboard header, the pump ignores the output register.
        if (followsHeader == true)
        {
            // Owed back once written, even unanswered: the next status poll would already read "not following".
            Exchange(HydroShift2CurveProtocol.EncodeHeaderFollow(false), out var written);
            if (written)
            {
                lock (_lock) { _followsHeader = false; }
                _restoreHeaderFollow = true;
            }
        }
        // A driven target counts as latched even unanswered.
        Exchange(HydroShift2CurveProtocol.EncodePumpOutput(HydroShift2CurveProtocol.PumpOutputForDuty(duty.Value)));
        _pumpDriven = true;
    }

    /// <summary>Whether the pump follows the motherboard header while Nexus is not driving it.</summary>
    public bool FollowsMotherboardWhenIdle
    {
        get
        {
            lock (_lock)
            {
                return _store.Load().Devices.HydroShift2Curve.PumpFollowsMotherboard
                    ?? (_pumpDriven ? _restoreHeaderFollow : _followsHeader == true);
            }
        }
    }

    /// <summary>Puts an idle pump on the user's follow choice when the board reports otherwise.</summary>
    private void ApplyIdleFollow()
    {
        bool? follows;
        lock (_lock)
        {
            if (_pumpDuty is not null || _pumpDriven) return;
            follows = _followsHeader;
        }
        if (follows is null || _store.Load().Devices.HydroShift2Curve.PumpFollowsMotherboard is not { } wanted || wanted == follows)
        {
            return;
        }
        if (Exchange(HydroShift2CurveProtocol.EncodeHeaderFollow(wanted)) is null)
        {
            return;
        }
        if (!wanted)
        {
            Exchange(HydroShift2CurveProtocol.EncodePumpOutput(HydroShift2CurveProtocol.DefaultPumpOutput));
        }
        lock (_lock) { _followsHeader = wanted; }
    }

    /// <summary>Returns the pump to the user's follow choice, else to how Nexus found it. True once answered.</summary>
    private bool HandBackPump()
    {
        var follow = _store.Load().Devices.HydroShift2Curve.PumpFollowsMotherboard ?? _restoreHeaderFollow;
        var command = follow
            ? HydroShift2CurveProtocol.EncodeHeaderFollow(true)
            : HydroShift2CurveProtocol.EncodePumpOutput(HydroShift2CurveProtocol.DefaultPumpOutput);
        if (Exchange(command) is null)
        {
            return false;
        }
        _restoreHeaderFollow = false;
        return true;
    }

    private void SendLedsIfDirty()
    {
        byte[] leds;
        lock (_lock)
        {
            if (!_ledsDirty || _leds is null) return;
            leds = _leds;
            _ledsDirty = false;
        }
        foreach (var chunk in HydroShift2CurveProtocol.EncodeLedFrame(leds))
        {
            if (Exchange(chunk) is null)
            {
                lock (_lock)
                {
                    _ledsDirty |= ReferenceEquals(_leds, leds);
                }
                return;
            }
        }
    }

    private void TickMotors(long now)
    {
        if (now - _lastMotorPollAt < MotorPollMs)
        {
            return;
        }
        _lastMotorPollAt = now;

        foreach (var motor in Motors)
        {
            bool busy;
            lock (_lock) { busy = _motorBusy[motor]; }
            if (busy && Move(HydroShift2CurveProtocol.EncodeBusyProbe(motor)) == 0)
            {
                lock (_lock) { _motorBusy[motor] = false; }
            }
        }

        int step;
        bool started = false;
        lock (_lock)
        {
            if (_calibrateRequested && _homingStep < 0)
            {
                _calibrateRequested = false;
                _homingStep = 0;
                started = true;
            }
            step = _homingStep;
        }
        if (started)
        {
            // Persisted so a restart mid-way homes again instead of trusting a stale position.
            _store.Update(s => s.Devices.HydroShift2Curve.Recalibrating = true);
            ServiceLog.Info($"[{HydroShift2CurveLcdDriver.Id}] recalibrating head");
        }
        if (step >= 0)
        {
            TickHoming(step);
            return;
        }

        int tilt, slide, targetTilt, targetSlide;
        bool tiltBusy, slideBusy;
        lock (_lock)
        {
            (tilt, slide, targetTilt, targetSlide) = (_tilt, _slide, _targetTilt, _targetSlide);
            tiltBusy = _motorBusy[HydroShift2CurveProtocol.TiltMotor];
            slideBusy = _motorBusy[HydroShift2CurveProtocol.SlideMotor];
        }
        if (!tiltBusy && HydroShift2CurveProtocol.TiltMove(tilt, targetTilt) is { } tiltMove
            && Start(HydroShift2CurveProtocol.TiltMotor, tiltMove.Direction, tiltMove.Steps, HydroShift2CurveProtocol.MoveSpeed))
        {
            lock (_lock) { _tilt = targetTilt; }
            SavePosition();
        }
        if (!slideBusy && HydroShift2CurveProtocol.SlideMove(slide, targetSlide) is { } slideMove
            && Start(HydroShift2CurveProtocol.SlideMotor, slideMove.Direction, slideMove.Steps, HydroShift2CurveProtocol.MoveSpeed))
        {
            lock (_lock) { _slide = targetSlide; }
            SavePosition();
        }
    }

    /// <summary>Runs L-Connect's homing one step at a time, each only once both motors are idle.</summary>
    private void TickHoming(int step)
    {
        lock (_lock)
        {
            if (_motorBusy[HydroShift2CurveProtocol.TiltMotor] || _motorBusy[HydroShift2CurveProtocol.SlideMotor]) return;
        }
        if (step < HydroShift2CurveProtocol.HomingSteps.Length)
        {
            var (motor, direction, steps) = HydroShift2CurveProtocol.HomingSteps[step];
            if (Start(motor, direction, steps, HydroShift2CurveProtocol.HomeSpeed))
            {
                lock (_lock) { _homingStep = step + 1; }
            }
            return;
        }
        lock (_lock)
        {
            _homingStep = -1;
            _tilt = 0;
            _slide = 0;
        }
        SavePosition();
        ServiceLog.Info($"[{HydroShift2CurveLcdDriver.Id}] head recalibrated");
    }

    /// <summary>Starts a move; false when the board refused it, which marks the motor it named as busy.</summary>
    private bool Start(byte motor, byte direction, int steps, byte speed)
    {
        var reply = Exchange(HydroShift2CurveProtocol.EncodeMove(motor, direction, steps, speed), out var written);
        var status = reply is null ? null : HydroShift2CurveProtocol.DecodeMoveStatus(reply);
        if (status is null && written)
        {
            // The board took the move and only its answer was lost; resending a relative move would travel twice.
            ServiceLog.Warn($"[{HydroShift2CurveLcdDriver.Id}] motor {motor} move unanswered; counting it as started");
            status = 0;
        }
        lock (_lock)
        {
            if (status is { } running && running > 0 && running < _motorBusy.Length)
            {
                _motorBusy[running] = true;
            }
            if (status == 0)
            {
                _motorBusy[motor] = true;
            }
        }
        return status == 0;
    }

    private int? Move(byte[] command) =>
        Exchange(command) is { } reply ? HydroShift2CurveProtocol.DecodeMoveStatus(reply) : null;

    private void SavePosition()
    {
        int tilt, slide;
        bool homing;
        lock (_lock) { (tilt, slide, homing) = (_tilt, _slide, _homingStep >= 0); }
        _store.Update(s =>
        {
            s.Devices.HydroShift2Curve.Tilt = tilt;
            s.Devices.HydroShift2Curve.Slide = slide;
            s.Devices.HydroShift2Curve.Recalibrating = homing;
        });
    }

    private byte[]? Exchange(byte[] command) => Exchange(command, out _);

    /// <summary>Writes one command and returns the reply that echoes it, or null; <paramref name="written"/> says whether the board took the write.</summary>
    private byte[]? Exchange(byte[] command, out bool written)
    {
        written = false;
        var pipe = _pipe;
        if (pipe is null)
        {
            return null;
        }
        // Every move reply looks alike, so a late one left queued would answer the next move.
        if (command[0] == HydroShift2CurveProtocol.BoardMove)
        {
            for (int i = 0; i < 8 && pipe.Read(_reply, 1) > 0; i++) { }
        }
        if (!pipe.Write(command))
        {
            return null;
        }
        written = true;
        var deadline = Environment.TickCount64 + ReplyTimeoutMs;
        while (true)
        {
            var remaining = deadline - Environment.TickCount64;
            if (remaining <= 0)
            {
                return null;
            }
            int read = pipe.Read(_reply, (int)remaining);
            if (read < 0)
            {
                return null;
            }
            if (read > 0 && _reply[0] == command[0])
            {
                return _reply.AsSpan(0, read).ToArray();
            }
        }
    }

    private void RaiseAvailabilityChanged()
    {
        try
        {
            AvailabilityChanged?.Invoke();
        }
        catch (Exception ex)
        {
            ServiceLog.Warn($"[{HydroShift2CurveLcdDriver.Id}] availability subscriber failed: {ex.Message}");
        }
    }
}
