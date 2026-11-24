using System;

namespace Nexus.Service.Platform;

/// <summary>Holds the process timer resolution at 1ms for the duration of the
/// scope. LibreHardwareMonitor's CPU update sleeps 1ms per core; at the default
/// 15.6ms tick those sleeps dominate the whole sensor walk.</summary>
internal static partial class WindowsTimerResolution
{
    [System.Runtime.InteropServices.LibraryImport("winmm.dll", EntryPoint = "timeBeginPeriod")]
    private static partial uint TimeBeginPeriod(uint period);

    [System.Runtime.InteropServices.LibraryImport("winmm.dll", EntryPoint = "timeEndPeriod")]
    private static partial uint TimeEndPeriod(uint period);

    private const uint PeriodMs = 1;
    private const uint TimerrNoError = 0;

    internal static Scope Elevate() => new(TimeBeginPeriod(PeriodMs) == TimerrNoError);

    internal readonly struct Scope : IDisposable
    {
        private readonly bool _held;
        internal Scope(bool held) => _held = held;
        public void Dispose()
        {
            if (_held) TimeEndPeriod(PeriodMs);
        }
    }
}
