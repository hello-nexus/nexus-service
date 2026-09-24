using System;
using Nexus.Service.Peripherals.Hid;

namespace Nexus.Service.Peripherals.Nollie;

/// <summary>
/// A HID endpoint with nothing behind it, for the dev-tools simulated board:
/// every write succeeds and vanishes, so the worker, the writer and the
/// device page run their real paths against a controller that is not there.
/// </summary>
public sealed class SimulatedNollieDevice : IHidDevice
{
    /// <summary>Path prefix the connection worker uses to tell a simulated board from a hot-plug one.</summary>
    public const string PathPrefix = "sim:";

    public SimulatedNollieDevice(NollieDevice spec)
    {
        VendorId = spec.VendorId;
        ProductId = spec.ProductId;
        Path = $"{PathPrefix}{spec.VendorId:X4}:{spec.ProductId:X4}";
        Serial = $"SIM-{spec.ProductId:X4}";
    }

    public int VendorId { get; }
    public int ProductId { get; }
    public string Path { get; }
    public string? Serial { get; }
    public int UsagePage => NollieProtocol.VendorUsagePage;
    public int Usage => NollieProtocol.VendorUsage;

    public bool SetFeature(ReadOnlySpan<byte> report) => true;
    public bool GetFeature(Span<byte> buffer) => false;
    public bool GetInputReport(Span<byte> buffer) => false;
    public bool Write(ReadOnlySpan<byte> report) => true;
    public bool SetOutputReport(ReadOnlySpan<byte> report) => true;
    public int Read(Span<byte> buffer, int timeoutMs) => 0;
    public void Dispose() { }
}
