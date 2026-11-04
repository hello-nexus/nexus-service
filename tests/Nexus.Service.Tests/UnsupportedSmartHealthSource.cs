using System;
using Nexus.Service.Diagnostics.Storage;

namespace Nexus.Service.Tests;

/// <summary>SMART source that reports no support on every platform, so tests
/// never construct the LhmComputer-backed monitor (which opens hardware).</summary>
internal sealed class UnsupportedSmartHealthSource : ISmartHealthSource
{
    public SmartSnapshot Snapshot() => new() { Supported = false, Drives = Array.Empty<SmartDriveInfo>() };

    public void ForceRefresh() { }
}
