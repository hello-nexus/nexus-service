using System;
using System.Linq;
using Nexus.Service.Peripherals.Hid;
using Nexus.Service.Platform;

namespace Nexus.Service.Peripherals.LogitechAudio;

/// <summary>
/// The G560's subwoofer level over HID++ 2.0 (feature 0x8305 BassTone: function 0 reads
/// the level, function 1 sets it, 0-100), on the speaker's HID++ collection (usage page
/// 0xFF43). OpenRGB keeps the same collection open for the lighting; HID input reports
/// reach every open handle, and our software id keeps the two conversations apart.
/// </summary>
public sealed class HidppSpeakerBass : ILogitechSpeakerBass, IDisposable
{
    public const int LogitechVendorId = 0x046D;
    public const int G560ProductId = 0x0A78;
    private const int HidppUsagePage = 0xFF43;
    private const int ReplyTimeoutMs = 600;

    private readonly IHidEnumerator _hid;
    private readonly object _lock = new();
    private string? _path;
    private IHidDevice? _device;
    private byte _featureIndex;
    private bool _wasPresent;

    public HidppSpeakerBass(IHidEnumerator hid)
    {
        _hid = hid;
    }

    public int Generation { get; private set; }

    public bool IsPresent()
    {
        lock (_lock)
        {
            var path = Locate();
            if (path != _path)
            {
                Close();
                _path = path;
                _featureIndex = 0;
            }
            var present = path is not null;
            if (present && !_wasPresent)
            {
                Generation++;
            }
            _wasPresent = present;
            return present;
        }
    }

    public int? GetLevel()
    {
        lock (_lock)
        {
            var reply = Call(0, ReadOnlySpan<byte>.Empty);
            return reply is null ? null : Math.Clamp((int)reply[4], 0, 100);
        }
    }

    public bool SetLevel(int level)
    {
        lock (_lock)
        {
            var value = (byte)Math.Clamp(level, 0, 100);
            return Call(1, new[] { value }) is not null;
        }
    }

    public void Dispose()
    {
        lock (_lock)
        {
            Close();
        }
    }

    private string? Locate() =>
        _hid.Find(LogitechVendorId, G560ProductId)
            .FirstOrDefault(i => i.UsagePage == HidppUsagePage && (i.OutputReportByteLength == 0 || i.OutputReportByteLength >= Hidpp20.LongReportLength))
            ?.Path;

    // One BassTone function call; the reply report, or null. Caller holds _lock.
    private byte[]? Call(byte function, ReadOnlySpan<byte> parameters)
    {
        if (_path is null && (_path = Locate()) is null)
        {
            return null;
        }
        if (_featureIndex == 0)
        {
            var found = Transact(Hidpp20.GetFeatureRequest(Hidpp20.BassToneFeature));
            if (found is null || found[4] == 0)
            {
                return null;
            }
            _featureIndex = found[4];
        }
        return Transact(Hidpp20.Request(_featureIndex, function, parameters));
    }

    private byte[]? Transact(byte[] request)
    {
        _device ??= _path is null ? null : _hid.Open(_path, forInput: true);
        if (_device is null)
        {
            return null;
        }
        var buffer = new byte[64];
        // Leave out whatever is still queued: OpenRGB's replies and our own late ones.
        for (var i = 0; i < 64 && _device.Read(buffer, 0) > 0; i++)
        {
        }
        if (!_device.Write(request))
        {
            Close();
            return null;
        }
        var deadline = Environment.TickCount64 + ReplyTimeoutMs;
        while (true)
        {
            var left = (int)(deadline - Environment.TickCount64);
            if (left <= 0)
            {
                ServiceLog.Warn($"[logitech-speaker] HID++ feature 0x{request[2]:X2} function {request[3] >> 4}: no reply");
                return null;
            }
            var read = _device.Read(buffer, left);
            if (read < 0)
            {
                Close();
                return null;
            }
            if (read == 0)
            {
                continue;
            }
            var report = buffer.AsSpan(0, read);
            switch (Hidpp20.Classify(request, report))
            {
                case Hidpp20.ReplyKind.Answer:
                    return report.ToArray();
                case Hidpp20.ReplyKind.Error:
                    ServiceLog.Warn($"[logitech-speaker] HID++ feature 0x{request[2]:X2} function {request[3] >> 4}: error {report[5]}");
                    return null;
            }
        }
    }

    private void Close()
    {
        _device?.Dispose();
        _device = null;
    }
}
