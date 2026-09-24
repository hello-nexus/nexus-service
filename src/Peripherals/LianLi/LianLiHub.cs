using System;
using System.Threading;
using Nexus.Service.Peripherals.Hid;

namespace Nexus.Service.Peripherals.LianLi;

public sealed class LianLiHub : IDisposable
{
    private readonly object _lock = new();
    private readonly byte[] _colorReport = new byte[LianLiProtocol.OutputReportSize];
    private IHidDevice? _device;
    private LianLiFanProfile _profile = LianLiFanProfiles.Default;
    private volatile string _modelName = "";
    private bool _disposed;

    public string DeviceId => "lianli";

    public LianLiState State { get; } = new();

    public bool IsConnected => State.IsConnected;

    /// <summary>Short model name for the attached device, or empty when not connected.</summary>
    public string ModelName => _modelName;

    /// <summary>Layout + register profile of the attached hub; the SL-Infinity row while detached.</summary>
    public LianLiFanProfile Profile
    {
        get { lock (_lock) { return _profile; } }
    }

    public void Attach(IHidDevice device, LianLiFanProfile profile)
    {
        lock (_lock)
        {
            _device = device;
            _profile = profile;
            _modelName = profile.ModelName ?? "";
            State.IsConnected = true;
        }
    }

    public void Detach()
    {
        lock (_lock)
        {
            _device?.Dispose();
            _device = null;
            _profile = LianLiFanProfiles.Default;
            _modelName = "";
            State.IsConnected = false;
        }
    }

    public bool SetQuantity(int port, int qty)
    {
        lock (_lock)
        {
            if (_device == null) return false;
            return _device.SetFeature(LianLiProtocol.BuildSetQuantity(_profile, port, qty));
        }
    }

    public bool SetReleaseMode(int port)
    {
        lock (_lock)
        {
            if (_device == null) return false;
            return _device.SetFeature(LianLiProtocol.BuildReleaseMode(port, _profile.ManualRegister));
        }
    }

    public bool SetSpeed(int port, int duty)
    {
        lock (_lock)
        {
            if (_device == null) return false;
            // Mode write then duty write: firmware drops the duty byte if it arrives
            // before the manual-mode transition settles (FanControl.LianLi / L-Connect 3).
            if (!_device.SetFeature(LianLiProtocol.BuildManualMode(port, _profile.ManualRegister))) return false;
            Thread.Sleep(LianLiProtocol.FanCommandSettleMs);
            if (!_device.SetFeature(LianLiProtocol.BuildSetSpeed(port, duty, _profile.FlooredDuty))) return false;
            State.Duty[port] = duty;
            return true;
        }
    }

    /// <summary>
    /// Refresh the duty on a port already in manual mode - the speed write only,
    /// no manual-mode re-entry and no settle. Re-entering manual mode resets the
    /// fan to its default, so the periodic re-assert must not call <see
    /// cref="SetSpeed"/> (which re-enters): that pins the fan at its default and
    /// the duty never takes. Use this for re-assertion; use SetSpeed to first
    /// take a port off mobo PWM.
    /// </summary>
    public bool SetDuty(int port, int duty)
    {
        lock (_lock)
        {
            if (_device == null) return false;
            if (!_device.SetFeature(LianLiProtocol.BuildSetSpeed(port, duty, _profile.FlooredDuty))) return false;
            State.Duty[port] = duty;
            return true;
        }
    }

    // Pads a short E0 command into the OutputReportSize scratch buffer and sends
    // it interrupt-OUT. Caller holds _lock.
    private bool WriteCommand(ReadOnlySpan<byte> command)
    {
        // Feature report, matching L-Connect's SetEffectSetting. Same reason as
        // SendColorData: the interrupt-OUT path does not reach the LEDs.
        return _device!.SetFeature(command);
    }

    // Output-report scratch for the per-frame start/commit commands. Kept
    // separate from _colorReport so a command never clobbers an in-flight frame.
    private readonly byte[] _cmdReport = new byte[LianLiProtocol.OutputReportSize];

    /// <summary>
    /// Per-frame "start" announcing the port + fan count before its color push:
    /// the family's quantity command. The firmware applies the streamed colors
    /// per frame only when each push is framed by this start; without it the
    /// panel re-renders on its own slow internal cadence (~0.6 Hz).
    /// </summary>
    public bool SendStartAction(int port, int fans)
    {
        lock (_lock)
        {
            if (_device == null) return false;
            return _device.SetFeature(LianLiProtocol.BuildSetQuantity(_profile, port, fans));
        }
    }

    public bool SendColorData(int ch, ReadOnlySpan<byte> leds)
    {
        lock (_lock)
        {
            if (_device == null) return false;
            LianLiProtocol.WriteColorData(_colorReport, ch, leds);
            // SL-Infinity colours stay on the control pipe its lighting was
            // verified over; the SL v1 takes them on interrupt-OUT.
            return _profile.ColorViaInterruptOut
                ? _device.Write(_colorReport)
                : _device.SetOutputReport(_colorReport);
        }
    }

    /// <summary>
    /// Per-channel commit applying the just-streamed colors (STATIC_COLOR). Sent
    /// as an OUTPUT report (matches OpenRGB SendCommitAction); a feature-report
    /// commit applies, but only on the firmware's slow internal cadence.
    /// </summary>
    public bool SendEffectCommit(int ch) =>
        SendModeCommit(ch, LianLiProtocol.EffectStatic, LianLiProtocol.SpeedDefault,
            LianLiProtocol.DirectionDefault, LianLiProtocol.BrightnessDefault);

    /// <summary>
    /// Commit a firmware effect on channel ch. E0 (0x10|ch) effect speed dir
    /// brightness, OUTPUT report. Firmware-generated modes (rainbow, breathing,
    /// etc.) animate on-chip from this single commit - no per-LED streaming.
    /// </summary>
    public bool SendModeCommit(int ch, byte effect, byte speed, byte dir, byte brightness)
    {
        lock (_lock)
        {
            if (_device == null) return false;
            return WriteCommand(LianLiProtocol.BuildEffectCommit(ch, effect, speed, dir, brightness));
        }
    }

    /// <summary>
    /// Latches the just-committed effect settings. L-Connect sends this once
    /// after applying every port, and speed/brightness changes do not take on
    /// the hardware without it.
    /// </summary>
    public bool SendStopMerge()
    {
        lock (_lock)
        {
            if (_device == null) return false;
            return _device.SetFeature(LianLiProtocol.BuildStopMerge());
        }
    }

    public bool SendFrameSync()
    {
        lock (_lock)
        {
            if (_device == null) return false;
            return _device.SetFeature(LianLiProtocol.BuildFrameSync());
        }
    }

    public bool ReadRpm()
    {
        lock (_lock)
        {
            if (_device == null) return false;
            if (!_device.SetFeature(LianLiProtocol.BuildRpmPrimer())) return false;
            Span<byte> buf = stackalloc byte[LianLiProtocol.InputReportSize];
            buf[0] = LianLiProtocol.ReportId;
            if (!_device.GetInputReport(buf)) return false;
            for (var i = 0; i < LianLiProtocol.PortCount; i++)
            {
                var rpm = LianLiProtocol.DecodeRpm(buf, i, _profile.RpmOffset);
                if (rpm >= 0)
                {
                    State.Rpm[i] = rpm;
                }
            }
            return true;
        }
    }

    public void Dispose()
    {
        lock (_lock)
        {
            if (_disposed) return;
            _disposed = true;
            _device?.Dispose();
            _device = null;
        }
    }
}
