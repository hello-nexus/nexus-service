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

            var devices = _hid.Find(VendorId, ProductId);
            if (devices.Count == 0) return false;

            // The keeb exposes multiple HID interfaces. Filter by their report
            // sizes: feature interface has 9-byte feature reports; input
            // interface has 8-byte interrupt-IN reports. Some firmwares
            // present these on separate HID collections under the same VID/PID.
            var featureInfo = devices.FirstOrDefault(d => d.FeatureReportByteLength == FeatureReportLength);
            var inputInfo = devices.FirstOrDefault(d => d.InputReportByteLength == InputReportLength);
            if (featureInfo is null || inputInfo is null) return false;

            var feature = _hid.Open(featureInfo.Path);
            var input = _hid.Open(inputInfo.Path);
            if (feature is null || input is null)
            {
                feature?.Dispose();
                input?.Dispose();
                return false;
            }

            try
            {
                _keeb = new KeebTkl(feature, input);
                return true;
            }
            catch
            {
                feature.Dispose();
                input.Dispose();
                _keeb = null;
                return false;
            }
        }
    }

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
