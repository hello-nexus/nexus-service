using System;
using System.Diagnostics;
using System.Text;
using System.Threading;
using Nexus.Service.Peripherals.Hid;
using Nexus.Service.Platform;

namespace Nexus.Service.Peripherals.LianLi;

/// <summary>
/// Bounded service-log trace of what an SL V2 hub is sent: every write for a window after attach, plus a line
/// budget granted around each of the first firmware applies. It exists to settle which command a hub ignores.
/// </summary>
public sealed class LianLiWireTrace
{
    private const int AttachWindowMs = 30_000;
    private const int AttachLines = 400;
    private const int ApplyLines = 90;
    private const int MaxTracedApplies = 12;
    private const int ShownBytes = 8;
    private const int ApplyWindowMs = 3000;
    private const int MaxTracedAttaches = 5;
    private const int MaxLifetimeLines = 3000;

    private readonly string _hubId;

    /// <summary>Where trace lines go; the service log unless a test replaces it.</summary>
    internal Action<string> Sink { get; set; } = ServiceLog.Info;
    private long _attachedTs;
    private long _windowUntilTs;
    private long _applyUntilTs;
    private int _lines;
    private int _applies;
    private int _attaches;
    private int _lifetimeLines;

    public LianLiWireTrace(string hubId)
    {
        _hubId = hubId;
    }

    public void Reset()
    {
        Volatile.Write(ref _windowUntilTs, 0);
        Volatile.Write(ref _applyUntilTs, 0);
        Volatile.Write(ref _lines, 0);
        Volatile.Write(ref _applies, 0);
    }

    public void BeginAttachWindow()
    {
        if (Interlocked.Increment(ref _attaches) > MaxTracedAttaches)
        {
            return;
        }
        _attachedTs = Stopwatch.GetTimestamp();
        Volatile.Write(ref _applies, 0);
        Volatile.Write(ref _lines, AttachLines);
        Volatile.Write(ref _windowUntilTs, _attachedTs + AttachWindowMs * Stopwatch.Frequency / 1000);
        Sink($"[lianli-wire] {_hubId} tracing every write for the next {AttachWindowMs / 1000} s");
    }

    /// <summary>Grants the next apply's writes a line budget; only the first few applies after attach are traced.</summary>
    public void BeginApply(string what)
    {
        if (Interlocked.Increment(ref _applies) > MaxTracedApplies)
        {
            return;
        }
        Volatile.Write(ref _lines, Math.Max(Volatile.Read(ref _lines), ApplyLines));
        Volatile.Write(ref _applyUntilTs, Stopwatch.GetTimestamp() + ApplyWindowMs * Stopwatch.Frequency / 1000);
        Sink($"[lianli-wire] {_hubId} apply {what}");
    }

    public void Note(string kind, ReadOnlySpan<byte> report, bool ok)
    {
        if (Volatile.Read(ref _lines) <= 0)
        {
            return;
        }
        var now = Stopwatch.GetTimestamp();
        var inWindow = now < Volatile.Read(ref _windowUntilTs) || now < Volatile.Read(ref _applyUntilTs);
        if (!inWindow || Volatile.Read(ref _lifetimeLines) >= MaxLifetimeLines)
        {
            return;
        }
        if (Interlocked.Decrement(ref _lines) < 0)
        {
            return;
        }
        Interlocked.Increment(ref _lifetimeLines);
        var sb = new StringBuilder(64);
        sb.Append("[lianli-wire] ").Append(_hubId).Append(" +")
          .Append((long)Stopwatch.GetElapsedTime(_attachedTs, now).TotalMilliseconds).Append(" ms ")
          .Append(kind).Append(' ');
        var n = Math.Min(report.Length, ShownBytes);
        for (var i = 0; i < n; i++)
        {
            sb.Append(report[i].ToString("X2"));
        }
        sb.Append(" len=").Append(report.Length).Append(ok ? " ok" : " FAIL");
        Sink(sb.ToString());
    }
}

/// <summary>Forwards to the real device and reports each write and its result to a <see cref="LianLiWireTrace"/>.</summary>
internal sealed class TracingHidDevice : IHidDevice
{
    private readonly IHidDevice _inner;
    private readonly LianLiWireTrace _trace;

    public TracingHidDevice(IHidDevice inner, LianLiWireTrace trace)
    {
        _inner = inner;
        _trace = trace;
    }

    public int VendorId => _inner.VendorId;
    public int ProductId => _inner.ProductId;
    public string Path => _inner.Path;
    public string? Serial => _inner.Serial;
    public int UsagePage => _inner.UsagePage;
    public int Usage => _inner.Usage;

    public bool SetFeature(ReadOnlySpan<byte> report)
    {
        var ok = _inner.SetFeature(report);
        _trace.Note("feature", report, ok);
        return ok;
    }

    public bool GetFeature(Span<byte> buffer) => _inner.GetFeature(buffer);

    public bool GetInputReport(Span<byte> buffer)
    {
        var ok = _inner.GetInputReport(buffer);
        _trace.Note("getinput", buffer, ok);
        return ok;
    }

    public bool Write(ReadOnlySpan<byte> report)
    {
        var ok = _inner.Write(report);
        _trace.Note("write", report, ok);
        return ok;
    }

    public bool SetOutputReport(ReadOnlySpan<byte> report)
    {
        var ok = _inner.SetOutputReport(report);
        _trace.Note("output", report, ok);
        return ok;
    }

    public int Read(Span<byte> buffer, int timeoutMs) => _inner.Read(buffer, timeoutMs);

    public void Dispose() => _inner.Dispose();
}
