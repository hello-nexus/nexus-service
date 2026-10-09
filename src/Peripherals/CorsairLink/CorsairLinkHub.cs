using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Nexus.Service.Peripherals.Hid;
using Nexus.Service.Platform;

namespace Nexus.Service.Peripherals.CorsairLink;

/// <summary>
/// Owns the iCUE LINK System Hub HID device and serializes all access. Holds the
/// hub in software/direct mode for its connected lifetime (handing control back
/// to firmware on detach), enumerates the daisy chain, reads speed/temperature
/// telemetry, sets fan duty, and streams per-LED color. Every public operation
/// runs the full close->open->io->close endpoint sequence under <see cref="_lock"/>.
/// </summary>
public sealed class CorsairLinkHub : IDisposable
{
    private const int ReadTimeoutMs = 250;

    // Drain reads pull reports the hub has already queued, so they return at once
    // or the queue is empty; a short timeout caps how long a no-ack write holds
    // _lock (shared with the 30Hz SendColors) while realigning the stream.
    private const int ResyncReadTimeoutMs = 50;
    // Caps the detach drain while another program keeps the hub answering.
    private const int DrainReads = 8;

    private readonly object _lock = new();
    private readonly byte[] _write = new byte[CorsairLinkProtocol.WriteBufferLength];
    // _readRaw receives the full interrupt-IN report (report-id byte at [0]); _read
    // holds it stripped of that byte, the layout the parsers are calibrated to.
    private readonly byte[] _readRaw = new byte[CorsairLinkProtocol.WriteBufferLength];
    private readonly byte[] _read = new byte[CorsairLinkProtocol.ReportLength];
    // Color stream scratch: 6-byte inner header + the concatenated RGB. 8192 caps
    // the chain at ~2728 LEDs, far above a full 24-device chain; SendColors drops
    // a frame that would overflow it rather than corrupt the stream.
    private readonly byte[] _colorInner = new byte[8192];

    private IHidDevice? _device;
    private bool _softwareMode;
    // Firmware major byte from getFirmware; gates the LED port-power split
    // (channel >=13 on fw >=2, >=7 on fw <2, lsh.go:3982).
    private int _firmwareMajor;
    // Streaming is primed (Initialize ran, hub connected).
    private bool _colorPrimed;
    // The hub refused a colour write; the handle answers 03 until reopened.
    private bool _colorReopenDue;
    private long _colorReopenAfterMs;
    internal const int ColorReopenBackoffMs = 1000;
    internal Func<long> NowMs { get; set; } = () => Environment.TickCount64;
    internal int DetectionPollMs { get; set; } = CorsairLinkProtocol.DetectionPollMs;
    private bool _disposed;
    // A re-detection owns the hub: colour, duty and telemetry I/O stand down.
    private bool _redetecting;
    private volatile bool _redetectRequested;

    /// <param name="idPrefix">"corsair:" keeps the single-hub ids; other hubs carry their serial.</param>
    /// <param name="number">1-based display number; 1 for the hub holding the unqualified ids.</param>
    public CorsairLinkHub(string idPrefix = "corsair:", int number = 1)
    {
        IdPrefix = idPrefix;
        Number = number;
    }

    /// <summary>The Nexus Control / device-row id every hub shares.</summary>
    public string DeviceId => "corsair";

    public string IdPrefix { get; }
    public int Number { get; }

    /// <summary>"corsair" for the first hub, "corsair:&lt;serial&gt;" for the others.</summary>
    public string HubId => IdPrefix.TrimEnd(':');

    public string ChannelId(int channel) => $"{IdPrefix}ch{channel}";

    public string PortLabel(int channel) => Number == 1 ? $"Port {channel}" : $"Hub {Number} Port {channel}";

    public bool Redetecting
    {
        get { lock (_lock) return _redetecting; }
    }

    public bool RedetectRequested => _redetectRequested;

    /// <summary>Queues a re-detection; the hub's connection worker runs it on its next poll.</summary>
    public bool RequestRedetect()
    {
        if (!IsConnected || Redetecting || _redetectRequested) return false;
        _redetectRequested = true;
        return true;
    }

    public CorsairLinkState State { get; } = new();

    public bool IsConnected => State.IsConnected;

    /// <summary>Listens without writing. The hub never reports unprompted, so any input report answers another program's command.</summary>
    public static bool HearsAnotherHost(IHidDevice device, int listenMs)
    {
        var buffer = new byte[CorsairLinkProtocol.WriteBufferLength];
        return device.Read(buffer, listenMs) > 0;
    }

    /// <summary>Another program answered on the hub; every write is refused from then on, since the hub's handles are shared by all hosts.</summary>
    public bool ForeignHostSeen { get; private set; }

    public void Attach(IHidDevice device)
    {
        lock (_lock)
        {
            _device = device;
            ForeignHostSeen = false;
            _softwareMode = false;
            _firmwareMajor = 0;
            _colorPrimed = false;
            _colorReopenDue = false;
            _redetecting = false;
            _redetectRequested = false;
        }
    }

    /// <param name="handBack">False when a vendor app now owns the hub: switching it to hardware mode would cut that app off.</param>
    public void Detach(bool handBack = true)
    {
        lock (_lock)
        {
            if (_device != null && _softwareMode && handBack && !ForeignHostSeen)
            {
                // Hand the chain back to firmware so the fans keep running on the
                // hub's own curve once Nexus lets go.
                Transfer(CorsairLinkProtocol.CmdHardwareMode);
            }
            // Drain replies still queued for this handle, or the next session's
            // listen hears them as another program.
            for (var i = 0; i < DrainReads && ReadStrippedLocked() > 0; i++) { }
            _device?.Dispose();
            _device = null;
            State.IsConnected = false;
            State.Firmware = "";
            State.Devices = Array.Empty<CorsairLinkDevice>();
            State.UnmappedChannels = Array.Empty<int>();
            _softwareMode = false;
            _colorPrimed = false;
        }
    }

    /// <summary>
    /// Enter software mode, read firmware, enumerate the chain, and open the color
    /// endpoint. Marks the hub connected on success. Returns false if the device
    /// stops responding mid-init.
    /// </summary>
    public bool Initialize()
    {
        lock (_lock)
        {
            if (_device == null) return false;

            var fw = Transfer(CorsairLinkProtocol.CmdGetFirmware);
            if (fw >= 8)
            {
                State.Firmware = $"{_read[4]}.{_read[5]}.{_read[6] | (_read[7] << 8)}";
                _firmwareMajor = _read[4];
            }

            Transfer(CorsairLinkProtocol.CmdSoftwareMode);
            // Firmware needs ~500 ms after entering software mode before it accepts
            // further commands (OpenLinkHub transferTimeout, lsh.go:4886).
            Thread.Sleep(CorsairLinkProtocol.SoftwareModeSettleMs);
            _softwareMode = true;

            if (!RefreshLocked()) return false;

            // Another host (iCUE, a crashed session) can leave handle 0 holding a
            // different resource, and an open on a busy handle is refused, so free
            // it first. It then stays open for the whole session.
            if (!OpenColorHandleLocked())
            {
                ServiceLog.Warn("[corsair] colour handle open refused");
                return false;
            }
            // Mixed QX+RX chains drop QX lighting without a 40 ms settle after the
            // first color endpoint open (OpenLinkHub lsh.go:4454).
            Thread.Sleep(40);
            _colorPrimed = true;
            _colorReopenDue = false;

            State.IsConnected = true;
            return true;
        }
    }

    /// <summary>Re-enumerate the chain and refresh speed/temperature telemetry. Catches hot-plug.</summary>
    public bool Poll()
    {
        lock (_lock)
        {
            if (_device == null || _redetecting) return false;
            return RefreshLocked();
        }
    }

    /// <summary>
    /// Re-maps the daisy chain (see <see cref="CorsairLinkProtocol.CmdStartDetection"/>)
    /// and re-reads it. False when the hub refused, never finished, or went away.
    /// </summary>
    public async Task<bool> RedetectAsync(CancellationToken ct)
    {
        lock (_lock)
        {
            _redetectRequested = false;
            if (_device == null || _redetecting) return false;
            _redetecting = true;
            var n = Transfer(CorsairLinkProtocol.CmdStartDetection);
            // A timed-out read may still have started detection; the pings below tell.
            if (n < 0 || (n > CorsairLinkProtocol.ResponseStatusOffset && Refused(CorsairLinkProtocol.CmdStartDetection)))
            {
                // Drain a late 1a echo while it still counts as ours, or it reads as another host.
                for (var i = 0; i < DrainReads && ReadStrippedLocked() > 0; i++) { }
                _redetecting = false;
                return false;
            }
        }
        try
        {
            var finished = false;
            for (var i = 0; i < CorsairLinkProtocol.DetectionMaxPolls && !finished; i++)
            {
                await Task.Delay(DetectionPollMs, ct).ConfigureAwait(false);
                lock (_lock)
                {
                    if (_device == null) return false;
                    var n = Transfer(CorsairLinkProtocol.CmdPing);
                    finished = n > CorsairLinkProtocol.ResponseStatusOffset
                        && _read[CorsairLinkProtocol.ResponseCommandOffset] == CorsairLinkProtocol.CmdPing[0]
                        && _read[CorsairLinkProtocol.ResponseBusyOffset] == 0;
                }
            }
            lock (_lock)
            {
                if (_device == null) return false;
                // The chain re-powered under the colour handle; reopen it on the next frame.
                _colorReopenDue = true;
                _colorReopenAfterMs = 0;
                return RefreshLocked() && finished;
            }
        }
        finally
        {
            lock (_lock) _redetecting = false;
        }
    }

    /// <summary>Set duty percent for the given channels in one packet. Retries on rejection.</summary>
    public bool SetDuties(IReadOnlyList<(int channel, int duty)> items)
    {
        lock (_lock)
        {
            if (_device == null || _redetecting || items.Count == 0) return false;
            var count = Math.Min(items.Count, CorsairLinkProtocol.MaxChannels);
            Span<byte> payload = stackalloc byte[1 + CorsairLinkProtocol.MaxChannels * 4];
            payload[0] = (byte)count;
            for (var i = 0; i < count; i++)
            {
                var (channel, duty) = items[i];
                var clamped = Math.Clamp(duty, 0, 100);
                var o = 1 + i * 4;
                payload[o] = (byte)channel;
                payload[o + 1] = 0x00;
                payload[o + 2] = (byte)clamped;
                payload[o + 3] = 0x00;
            }
            return WriteEndpointLocked(CorsairLinkProtocol.ModeSetSpeed,
                CorsairLinkProtocol.DataSetSpeed, payload.Slice(0, 1 + count * 4));
        }
    }

    /// <summary>
    /// Stream one frame of per-LED color. <paramref name="rgb"/> is every RGB
    /// device's LEDs concatenated in channel order, 3 bytes (R,G,B) each.
    /// </summary>
    public bool SendColors(ReadOnlySpan<byte> rgb)
    {
        lock (_lock)
        {
            if (_device == null || !_colorPrimed || _redetecting) return false;

            if (_colorReopenDue)
            {
                if (NowMs() < _colorReopenAfterMs) return false;
                if (!OpenColorHandleLocked())
                {
                    _colorReopenAfterMs = NowMs() + ColorReopenBackoffMs;
                    return false;
                }
                _colorReopenDue = false;
            }

            var len = 6 + rgb.Length;
            if (len > _colorInner.Length) return false;

            var bodyLen = rgb.Length + 2;
            _colorInner[0] = (byte)(bodyLen & 0xFF);
            _colorInner[1] = (byte)((bodyLen >> 8) & 0xFF);
            _colorInner[2] = 0x00;
            _colorInner[3] = 0x00;
            _colorInner[4] = CorsairLinkProtocol.DataSetColor[0];
            _colorInner[5] = CorsairLinkProtocol.DataSetColor[1];
            // The firmware treats a per-LED (0,0,0) as "no change" and holds the
            // LED's prior/default color, so a black LED stays lit instead of going
            // dark. Floor every channel to 1 (imperceptible) so each LED always
            // receives a definite value and a black frame reads as off.
            var dst = _colorInner.AsSpan(6, rgb.Length);
            for (var i = 0; i < dst.Length; i++)
            {
                dst[i] = rgb[i] == 0 ? (byte)1 : rgb[i];
            }

            var offset = 0;
            var first = true;
            while (offset < len)
            {
                var chunk = Math.Min(CorsairLinkProtocol.MaxColorChunk, len - offset);
                var cmd = first ? CorsairLinkProtocol.CmdWriteColor : CorsairLinkProtocol.CmdWriteSubColor;
                if (Transfer(cmd, _colorInner.AsSpan(offset, chunk)) <= 0) return false;
                if (Refused(cmd))
                {
                    _colorReopenDue = true;
                    _colorReopenAfterMs = NowMs() + ColorReopenBackoffMs;
                    return false;
                }
                offset += chunk;
                first = false;
            }
            return true;
        }
    }

    // Close then open handle 0 on the colour resource; true when the open is accepted.
    private bool OpenColorHandleLocked()
    {
        Span<byte> mode = stackalloc byte[] { CorsairLinkProtocol.ModeSetColor };
        Transfer(CorsairLinkProtocol.CmdCloseColorEndpoint);
        var n = Transfer(CorsairLinkProtocol.CmdOpenColorEndpoint, mode);
        return n > CorsairLinkProtocol.ResponseStatusOffset && !Refused(CorsairLinkProtocol.CmdOpenColorEndpoint);
    }

    // The last response answers this command with a non-zero status. A response
    // for another command (a stale queued report) is not read as a refusal.
    private bool Refused(ReadOnlySpan<byte> cmd) =>
        _read[CorsairLinkProtocol.ResponseCommandOffset] == cmd[0]
        && _read[CorsairLinkProtocol.ResponseStatusOffset] != 0;

    // ── internals (caller holds _lock) ──

    private bool RefreshLocked()
    {
        var devResp = ReadEndpointLocked(CorsairLinkProtocol.ModeGetDevices, CorsairLinkProtocol.DataGetDevices);
        if (devResp == null) return false;
        var discovered = CorsairLinkProtocol.ParseDevices(devResp);

        // Resolve dynamic LED counts for variable-LED devices (adapters, Commander Duo).
        var ledCounts = new int[CorsairLinkProtocol.SensorArrayLength];
        var ledsResp = ReadEndpointRawLocked(CorsairLinkProtocol.ModeGetLeds);
        if (ledsResp != null)
        {
            CorsairLinkProtocol.ParseLeds(ledsResp, ledCounts);
        }

        var speeds = new int[CorsairLinkProtocol.SensorArrayLength];
        var temps = new float[CorsairLinkProtocol.SensorArrayLength];
        Array.Fill(speeds, -1);
        Array.Fill(temps, float.NaN);

        var spResp = ReadEndpointLocked(CorsairLinkProtocol.ModeGetSpeeds, CorsairLinkProtocol.DataGetSpeeds);
        if (spResp != null) CorsairLinkProtocol.ParseSpeeds(spResp, speeds);
        var tpResp = ReadEndpointLocked(CorsairLinkProtocol.ModeGetTemperatures, CorsairLinkProtocol.DataGetTemperatures);
        if (tpResp != null) CorsairLinkProtocol.ParseTemperatures(tpResp, temps);

        var hasLcd = false;
        var list = new List<CorsairLinkDevice>(discovered.Count);
        var unmapped = new List<int>();
        foreach (var d in discovered)
        {
            var meta = CorsairLinkModels.Lookup(d.Type, d.Model);
            if (d.Type == 6 || d.Type == 14) hasLcd = true;
            // A hot-plugged device stays listed without a sensor/LED slot until the
            // chain is re-mapped; its speed sensor is the one slot readable for it.
            if (spResp != null && meta.HasSpeed && d.Channel < speeds.Length && speeds[d.Channel] < 0)
            {
                unmapped.Add(d.Channel);
            }
            var dynamicLeds = d.Channel < ledCounts.Length ? ledCounts[d.Channel] : 0;
            var dev = new CorsairLinkDevice
            {
                Channel = d.Channel,
                Type = d.Type,
                Model = d.Model,
                Name = meta.Name,
                Class = meta.Class,
                LedCount = meta.LedCount > 0 ? meta.LedCount : dynamicLeds,
                HasSpeed = meta.HasSpeed,
                HasTemperature = meta.HasTemperature,
                Serial = d.Serial,
                PortId = CorsairLinkProtocol.PortIdForChannel(d.Channel, _firmwareMajor),
                Rpm = d.Channel < speeds.Length ? speeds[d.Channel] : -1,
                TempC = d.Channel < temps.Length ? temps[d.Channel] : float.NaN,
            };
            list.Add(dev);
        }
        State.Devices = list;
        State.HasLcd = hasLcd;
        State.UnmappedChannels = unmapped;
        return true;
    }

    // close -> open -> read without dataType validation; for endpoints whose response
    // tag is undocumented (ModeGetLeds). Returns a copy or null on failure.
    private byte[]? ReadEndpointRawLocked(byte mode)
    {
        Span<byte> m = stackalloc byte[] { mode };
        Transfer(CorsairLinkProtocol.CmdCloseEndpoint, m);
        Transfer(CorsairLinkProtocol.CmdOpenEndpoint, m);
        var n = Transfer(CorsairLinkProtocol.CmdRead, m);
        byte[]? copy = null;
        if (n > 0) copy = _read.AsSpan(0, CorsairLinkProtocol.ReportLength).ToArray();
        Transfer(CorsairLinkProtocol.CmdCloseEndpoint, m);
        return copy;
    }

    // close -> open -> read one endpoint; returns a copy of the response (the
    // shared _read buffer is clobbered by the trailing close), or null on failure.
    private byte[]? ReadEndpointLocked(byte mode, ReadOnlySpan<byte> dataType)
    {
        Span<byte> m = stackalloc byte[] { mode };
        Transfer(CorsairLinkProtocol.CmdCloseEndpoint, m);
        Transfer(CorsairLinkProtocol.CmdOpenEndpoint, m);
        var n = Transfer(CorsairLinkProtocol.CmdRead, m);
        n = ResyncToDataType(n, dataType);
        byte[]? copy = null;
        if (n > 0 && _read[4] == dataType[0] && _read[5] == dataType[1])
        {
            copy = _read.AsSpan(0, CorsairLinkProtocol.ReportLength).ToArray();
        }
        Transfer(CorsairLinkProtocol.CmdCloseEndpoint, m);
        return copy;
    }

    // The matching response echoes its data-type at [4:6]; a mismatch is a stale
    // report queued by an earlier command. Drain with bare reads (no new command)
    // until it matches or the budget runs out. Mirrors OpenRGB's waitForDataType.
    private int ResyncToDataType(int n, ReadOnlySpan<byte> dataType)
    {
        var tries = 0;
        while (n > 0 && (_read[4] != dataType[0] || _read[5] != dataType[1])
               && tries < CorsairLinkProtocol.ReadResyncTries)
        {
            n = ReadStrippedLocked();
            tries++;
        }
        return n;
    }

    // Bare interrupt-IN read with no command write, report-id stripped like Transfer.
    private int ReadStrippedLocked()
    {
        if (_device == null) return -1;
        var n = _device.Read(_readRaw, ResyncReadTimeoutMs);
        if (n <= 0) return n;
        _readRaw.AsSpan(1, CorsairLinkProtocol.ReportLength).CopyTo(_read);
        NoteEcho(0);
        return n - 1;
    }

    // Every open HID handle receives every input report, so another program's
    // replies reach this one. Nexus sends only these commands after Initialize's
    // 02 13, whose reply can arrive late after a timed-out read; iCUE probes (09)
    // every file it opens.
    private bool IsOwnCommand(byte cmd) =>
        cmd is 0x01 or 0x05 or 0x06 or 0x07 or 0x08 or 0x0D
        || (cmd == 0x02 && !_colorPrimed)
        || (cmd is 0x12 or 0x1A && _redetecting);

    private void NoteEcho(byte sent)
    {
        var echo = _read[CorsairLinkProtocol.ResponseCommandOffset];
        if (echo != 0 && echo != sent && !IsOwnCommand(echo)) ForeignHostSeen = true;
    }

    // close -> open -> write(inner) -> close. Inner = [len_lo, len_hi, 0, 0,
    // dataType(2), data]. The hub applies a speed set on delivery and returns no
    // matchable ack, so success is not gated on the response; the trailing drain
    // realigns the shared response stream for the next telemetry read.
    private bool WriteEndpointLocked(byte mode, ReadOnlySpan<byte> dataType, ReadOnlySpan<byte> data)
    {
        Span<byte> m = stackalloc byte[] { mode };
        var len = 6 + data.Length;
        Span<byte> inner = stackalloc byte[1 + CorsairLinkProtocol.MaxChannels * 4 + 6];
        if (len > inner.Length) return false;
        var bodyLen = data.Length + 2;
        inner[0] = (byte)(bodyLen & 0xFF);
        inner[1] = (byte)((bodyLen >> 8) & 0xFF);
        inner[2] = 0x00;
        inner[3] = 0x00;
        inner[4] = dataType[0];
        inner[5] = dataType[1];
        data.CopyTo(inner.Slice(6));
        var frame = inner.Slice(0, len);

        Transfer(CorsairLinkProtocol.CmdCloseEndpoint, m);
        Transfer(CorsairLinkProtocol.CmdOpenEndpoint, m);
        var n = Transfer(CorsairLinkProtocol.CmdWrite, frame);
        if (n < 0) return false;
        ResyncToDataType(n, dataType);
        Transfer(CorsairLinkProtocol.CmdCloseEndpoint, m);
        return true;
    }

    private int Transfer(ReadOnlySpan<byte> command) => Transfer(command, default);

    private int Transfer(ReadOnlySpan<byte> command, ReadOnlySpan<byte> payload)
    {
        if (_device == null || ForeignHostSeen) return -1;
        Array.Clear(_write);
        _write[1] = 0x00;
        _write[2] = 0x01;
        var off = CorsairLinkProtocol.HeaderSize;
        command.CopyTo(_write.AsSpan(off));
        off += command.Length;
        if (!payload.IsEmpty) payload.CopyTo(_write.AsSpan(off));
        if (!_device.Write(_write)) return -1;
        var n = _device.Read(_readRaw, ReadTimeoutMs);
        if (n <= 0) return n;
        // Windows ReadFile returns the report with the report-id byte at [0]; hidapi
        // (which the parsers mirror) drops it. Strip it and report the stripped length.
        _readRaw.AsSpan(1, CorsairLinkProtocol.ReportLength).CopyTo(_read);
        NoteEcho(command[0]);
        return n - 1;
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
