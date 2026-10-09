using System;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Nexus.Service.Peripherals.Hid;
using Nexus.Service.Platform;

namespace Nexus.Service.Peripherals.StreamDeck;

/// <summary>
/// Keeps a blocking interrupt-IN read continuously pending for one connected
/// HID Stream Deck, on its own thread and its own HID handle - opened via
/// <see cref="IHidEnumerator.Open"/> with forInput true, independent of
/// <see cref="HidStreamDeckSurface"/>'s control handle used for image and
/// brightness writes. Mirrors KeebInputWorker's separate-handle rationale
/// (src/Peripherals/Hyte/Keeb/KeebInputWorker.cs): a read that is only
/// occasionally pending (the prior per-tick poll, cancelled every cycle) lets
/// presses land in the gaps and never registers them. A Stream Deck has no
/// single global singleton like the keeb hub, so one reader instance runs per
/// connected surface, started and stopped by StreamDeckConnectionWorker as
/// decks connect and disconnect.
/// </summary>
internal sealed class StreamDeckInputReader : IDisposable
{
    private const int DefaultReadTimeoutMs = 200;
    private const int DefaultRetryDelayMs = 1000;

    /// <summary>
    /// Bench diagnostic: logs the first few raw input reports per surface.
    /// The Mini sends a numbered report (byte 0 = 0x01 header, key states at
    /// bytes 1-6, no Windows 0x00 prepend), confirmed on hardware, so
    /// DecodeGen1Input's offset-1 is correct. Flip true to re-verify a model.
    /// </summary>
    private const bool LogRawInputReports = false;

    private const int MaxRawReportsLogged = 5;
    private const int RawPreviewByteCount = 8;

    private readonly IHidEnumerator _hid;
    private readonly string _path;
    private readonly StreamDeckModel _model;
    private readonly Action<StreamDeckInput> _onReport;
    private readonly int _readTimeoutMs;
    private readonly int _retryDelayMs;
    private readonly CancellationTokenSource _cts = new();
    private IHidDevice? _reader;
    private int _rawReportsLogged;

    public StreamDeckInputReader(
        IHidEnumerator hid, string path, StreamDeckModel model, Action<StreamDeckInput> onReport,
        int readTimeoutMs = DefaultReadTimeoutMs, int retryDelayMs = DefaultRetryDelayMs)
    {
        _hid = hid;
        _path = path;
        _model = model;
        _onReport = onReport;
        _readTimeoutMs = readTimeoutMs;
        _retryDelayMs = retryDelayMs;
        _ = Task.Run(() => Loop(_cts.Token));
    }

    private async Task Loop(CancellationToken ct)
    {
        var buf = new byte[_model.InputReportBufferLength];
        while (!ct.IsCancellationRequested)
        {
            try
            {
                if (_reader is null && !OpenReader())
                {
                    await Task.Delay(_retryDelayMs, ct).ConfigureAwait(false);
                    continue;
                }

                var n = _reader!.Read(buf, _readTimeoutMs);
                if (n == 0)
                {
                    continue; // idle: the read blocked up to _readTimeoutMs, nothing arrived
                }
                if (n < 0)
                {
                    // Device went away: the handle now fails reads instantly.
                    // Tear it down and back off so a physical unplug doesn't
                    // spin a core; OpenReader re-acquires when it returns, and
                    // StreamDeckConnectionWorker's reconcile drops the whole
                    // surface once _hid.Find stops enumerating this path.
                    CloseReader();
                    try { await Task.Delay(_retryDelayMs, ct).ConfigureAwait(false); }
                    catch (OperationCanceledException) { break; }
                    continue;
                }

                if (LogRawInputReports && _rawReportsLogged < MaxRawReportsLogged)
                {
                    _rawReportsLogged++;
                    ServiceLog.Info($"[streamdeck] raw input len={n} bytes={HexPreview(buf.AsSpan(0, n))}");
                }

                var input = StreamDeckProtocol.DecodeInput(buf.AsSpan(0, n), _model);
                if (input is not null)
                {
                    _onReport(input);
                }
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                ServiceLog.Error($"[streamdeck-input] read loop error ({_path}): {ex.GetType().Name}: {ex.Message}");
                CloseReader();
                try { await Task.Delay(_retryDelayMs, ct).ConfigureAwait(false); }
                catch (OperationCanceledException) { break; }
            }
        }
        CloseReader();
    }

    private bool OpenReader()
    {
        var dev = _hid.Open(_path, forInput: true);
        if (dev is null)
        {
            ServiceLog.Warn($"[streamdeck] input reader open FAILED ({_path})");
            return false;
        }
        _reader = dev;
        ServiceLog.Info($"[streamdeck] input reader opened ({_path})");
        return true;
    }

    private void CloseReader()
    {
        try { _reader?.Dispose(); } catch { /* best effort */ }
        _reader = null;
    }

    private static string HexPreview(ReadOnlySpan<byte> data)
    {
        var count = Math.Min(data.Length, RawPreviewByteCount);
        var sb = new StringBuilder(count * 3);
        for (var i = 0; i < count; i++)
        {
            if (i > 0)
            {
                sb.Append(' ');
            }
            sb.Append(data[i].ToString("X2"));
        }
        return sb.ToString();
    }

    /// <summary>
    /// Signals the read loop to stop; does not block. The loop observes
    /// cancellation within one _readTimeoutMs/_retryDelayMs cycle and closes
    /// its own handle - never joined synchronously here, since callers hold
    /// StreamDeckConnectionWorker's _lock and the loop's callback also
    /// acquires it, so a blocking join could deadlock against it.
    /// </summary>
    public void Dispose() => _cts.Cancel();
}
