using System;
using System.Linq;
using Qos.Service.Peripherals.Hid;
using Qos.Service.Peripherals.Keeb.Hid;

namespace Qos.Service.Peripherals.Keeb;

/// <summary>
/// Owns the open <see cref="KeebTkl"/> session — opens the two HID interfaces
/// (feature + input) on hot-swap, disposes them on disconnect, and exposes
/// the active device to the provider. Thread-safe: every accessor goes
/// through a single lock.
///
/// Why not bake this into <see cref="KeebProvider"/>: the provider has
/// per-request lifetime in DI terms (it's a singleton but its methods run
/// on different threads), and the HID device needs a stable owner that
/// outlives any single request. The session is a Singleton that the
/// hot-swap host drives, and the provider just reads the current device.
/// </summary>
public sealed class KeebSession : IDisposable
{
    private const int VendorId = 0x3402;
    private const int ProductId = 0x0300;
    private const int FeatureReportLength = 9;
    private const int InputReportLength = 8;

    private readonly IHidEnumerator _hid;
    private readonly object _lock = new();
    private KeebTkl? _keeb;
    private bool _disposed;

    public KeebSession(IHidEnumerator hid)
    {
        _hid = hid;
    }

    /// <summary>The currently-open keeb, or null if no Keeb TKL is attached.</summary>
    public KeebTkl? Keeb
    {
        get { lock (_lock) return _keeb; }
    }

    /// <summary>
    /// Try to open the keeb if one isn't already attached. Idempotent.
    /// Returns true if a session is currently open after this call.
    /// </summary>
    public bool TryOpen()
    {
        lock (_lock)
        {
            if (_disposed) return false;
            if (_keeb != null) return true;

            // The Keeb TKL exposes multiple HID interfaces. We match the legacy
            // nexus-control-service filter: pick devices with 9-byte feature
            // reports for the vendor control interface, and devices with
            // 8-byte input reports for the event interface. Two separate
            // pools — paired into (feature, input) by index so we never open
            // the same path twice (ReadFile would race with the background
            // input reader and block the feature-response read forever).
            var all = _hid.Find(VendorId, ProductId);
            Console.Error.WriteLine($"[keeb] {all.Count} HID interfaces for {VendorId:X4}:{ProductId:X4}");
            foreach (var d in all)
            {
                Console.Error.WriteLine($"[keeb]   feature={d.FeatureReportByteLength} input={d.InputReportByteLength} output={d.OutputReportByteLength} usagePage=0x{d.UsagePage:X4} usage=0x{d.Usage:X4} path=...{Tail(d.Path, 48)}");
            }

            // Feature interface: the only collection with a 9-byte feature
            // report. Bench-confirmed on T1: usagePage 0xFF11 / usage 0xF0,
            // feature=9, input=65, output=65. SetFeature lives here; 65-byte
            // responses arrive on this interface's interrupt-IN endpoint.
            //
            // Input interface: an 8-byte interrupt-IN endpoint on a
            // vendor-defined usagePage (0xFFxx). Bench-confirmed: 0xFF02 /
            // 0xF0 with feature=0, input=8, output=0. The HID mouse also
            // reports input=8 but lives on usagePage 0x0001 (generic
            // desktop) - filter by usagePage>=0xFF00 to skip it.
            var featureCandidates = all
                .Where(d => d.FeatureReportByteLength == FeatureReportLength)
                .ToList();
            var inputCandidates = all
                .Where(d => d.InputReportByteLength == InputReportLength && d.UsagePage >= 0xFF00)
                .ToList();

            if (featureCandidates.Count == 0 || inputCandidates.Count == 0)
            {
                Console.Error.WriteLine($"[keeb] missing candidates: feature={featureCandidates.Count}, input={inputCandidates.Count}. Skipping session open.");
                return false;
            }

            var featureInfo = featureCandidates[0];
            var inputInfo = inputCandidates.FirstOrDefault(d => d.Path != featureInfo.Path)
                            ?? inputCandidates[0];

            Console.Error.WriteLine($"[keeb] picked feature=...{Tail(featureInfo.Path, 32)} input=...{Tail(inputInfo.Path, 32)}");

            var feature = _hid.Open(featureInfo.Path);
            var input = _hid.Open(inputInfo.Path);
            if (feature is null || input is null)
            {
                feature?.Dispose();
                input?.Dispose();
                Console.Error.WriteLine("[keeb] failed to open one or both vendor interfaces.");
                return false;
            }

            try
            {
                _keeb = new KeebTkl(feature, input);
                Console.Error.WriteLine($"[keeb] session opened (fw {_keeb.FirmwareVersion}, layout {_keeb.Layout}).");
                return true;
            }
            catch (Exception e)
            {
                Console.Error.WriteLine($"[keeb] KeebTkl ctor failed: {e.GetType().Name}: {e.Message}");
                feature.Dispose();
                input.Dispose();
                _keeb = null;
                return false;
            }
        }
    }

    private static string Tail(string s, int n) => s.Length <= n ? s : s.Substring(s.Length - n);

    /// <summary>Close + dispose the open keeb, if any. Idempotent.</summary>
    public void Close()
    {
        lock (_lock)
        {
            _keeb?.Dispose();
            _keeb = null;
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Close();
    }
}
