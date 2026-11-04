using System;
using System.Threading;

namespace Nexus.Service.Sensors;

/// <summary>
/// Windows activation state for the system.specs app action. Unlike the specs
/// snapshot, which lives for the process, activation can change while the
/// service runs, so the answer is re-read after a short interval.
/// </summary>
internal static class WindowsActivation
{
    private static readonly TimeSpan Ttl = TimeSpan.FromMinutes(5);
    private static readonly Lock Gate = new();
    private static bool? _value;
    private static DateTime _readAtUtc = DateTime.MinValue;

    public static bool? Get()
    {
        if (!OperatingSystem.IsWindows()) return null;
        lock (Gate)
        {
            if (DateTime.UtcNow - _readAtUtc < Ttl) return _value;
            try
            {
                _value = WindowsSpecsNative.ReadActivation();
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[system-specs] {ex.GetType().Name}: {ex.Message}");
                _value = null;
            }
            _readAtUtc = DateTime.UtcNow;
            return _value;
        }
    }
}
