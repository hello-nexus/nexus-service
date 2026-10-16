namespace Nexus.Service.Sensors.Astral;

/// <summary>
/// NVAPI access the Astral telemetry supplement needs, behind an interface so
/// AstralGpuSupplement's gating/caching logic is testable without an NVIDIA
/// driver. AstralNvApiClient is the real Windows-only P/Invoke implementation;
/// it is the only thing that talks to nvapi64.dll.
/// </summary>
internal interface IAstralNvApiClient
{
    /// <summary>
    /// PCI subsystem id (vendor in the low portion) for the NVAPI physical GPU
    /// at <paramref name="adapterIndex"/>, matching the same enumeration order
    /// LibreHardwareMonitor's NvidiaGroup uses to assign its adapter indices.
    /// </summary>
    bool TryGetPciSubsystemId(int adapterIndex, out uint subSystemId);

    /// <summary>
    /// Reads the Astral 12VHPWR telemetry block (see AstralTelemetryParser.
    /// BlockSize). False on any NVAPI error (wrong adapter index, no Astral
    /// controller, I2C NAK); `block` is then empty.
    /// </summary>
    bool TryReadAstralBlock(int adapterIndex, out byte[] block);

    /// <summary>
    /// Reads the Astral G-sensor block from the same controller (see
    /// AstralGSensorParser.BlockSize). False on any NVAPI error; `block` is
    /// then empty.
    /// </summary>
    bool TryReadGSensorBlock(int adapterIndex, out byte[] block);
}
