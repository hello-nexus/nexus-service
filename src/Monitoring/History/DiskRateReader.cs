using System;
#if WINDOWS
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
#endif

namespace Nexus.Service.Monitoring.History;

/// <summary>One byte/s rate reading; both null when no prior reading exists
/// yet to diff against, or the diff was rejected (see ComputeRate).</summary>
public readonly record struct DiskRate(double? ReadBytesPerSec, double? WriteBytesPerSec);

/// <summary>
/// System-wide disk byte-rate gauge sampled by MetricsSampler each tick.
/// Windows: sums IOCTL_DISK_PERFORMANCE's cumulative BytesRead/BytesWritten
/// counters across every \\.\PhysicalDriveN. macOS: sums the
/// IOBlockStorageDriver Statistics counters via MacDiskStats. Linux: sums
/// /proc/diskstats sectors across physical disks via LinuxDiskStats. All
/// diff against the previous read using Environment.TickCount64 (monotonic,
/// immune to wall-clock adjustments) - the same contract as
/// NetworkRateReader.
/// </summary>
public sealed class DiskRateReader
{
    // Mirrors NetworkRateReader.MaxElapsedMs: a reading gap wider than this
    // (missed tick, system suspend) makes the byte delta span more than the
    // assumed 1-second window, so the read is discarded instead of reported
    // as a rate spike/trough.
    private const long MaxElapsedMs = 5000;

#if WINDOWS || MACOS || LINUX
    private long _lastTicks = -1;
    private long _lastBytesRead;
    private long _lastBytesWritten;
#endif

    private readonly Func<bool> _liveActivity;

    public DiskRateReader() : this(null) { }

    /// <param name="liveActivity">Windows only: false stops every per-second open and IOCTL of the physical drives, which keeps them awake.</param>
    public DiskRateReader(Func<bool>? liveActivity) => _liveActivity = liveActivity ?? (() => true);

    public DiskRate Read()
    {
#if WINDOWS
        if (!_liveActivity())
        {
            _lastTicks = -1;
            return new DiskRate(null, null);
        }
        var (bytesRead, bytesWritten) = ReadCumulativeCounters();
        var nowTicks = Environment.TickCount64;
        var rate = ComputeRate(_lastTicks, _lastBytesRead, _lastBytesWritten, nowTicks, bytesRead, bytesWritten);
        _lastTicks = nowTicks;
        _lastBytesRead = bytesRead;
        _lastBytesWritten = bytesWritten;
        return rate;
#elif MACOS || LINUX
        // A failed pass keeps the previous baseline; the next successful
        // read diffs across the real measured gap (discarded by ComputeRate
        // only once it exceeds MaxElapsedMs).
#if MACOS
        if (!Platform.Mac.MacDiskStats.TryReadCumulativeBytes(out var bytesRead, out var bytesWritten))
#else
        if (!Platform.Linux.LinuxDiskStats.TryReadCumulativeBytes(out var bytesRead, out var bytesWritten))
#endif
        {
            return new DiskRate(null, null);
        }
        var nowTicks = Environment.TickCount64;
        var rate = ComputeRate(_lastTicks, _lastBytesRead, _lastBytesWritten, nowTicks, bytesRead, bytesWritten);
        _lastTicks = nowTicks;
        _lastBytesRead = bytesRead;
        _lastBytesWritten = bytesWritten;
        return rate;
#else
        return new DiskRate(null, null);
#endif
    }

    /// <summary>Pure delta math: null on the first read (lastTicks &lt; 0),
    /// a non-positive or over-threshold elapsed gap (MaxElapsedMs), or a
    /// negative byte delta (counter reset, e.g. a drive re-enumerated).</summary>
    internal static DiskRate ComputeRate(
        long lastTicks, long lastBytesRead, long lastBytesWritten,
        long nowTicks, long nowBytesRead, long nowBytesWritten)
    {
        if (lastTicks < 0)
        {
            return new DiskRate(null, null);
        }

        var elapsedMs = nowTicks - lastTicks;
        if (elapsedMs <= 0 || elapsedMs > MaxElapsedMs)
        {
            return new DiskRate(null, null);
        }

        var deltaRead = nowBytesRead - lastBytesRead;
        var deltaWritten = nowBytesWritten - lastBytesWritten;
        if (deltaRead < 0 || deltaWritten < 0)
        {
            return new DiskRate(null, null);
        }

        var seconds = elapsedMs / 1000.0;
        return new DiskRate(deltaRead / seconds, deltaWritten / seconds);
    }

#if WINDOWS
    // Physical drives are numbered contiguously from 0; ReadCumulativeCounters
    // stops at the first index CreateFileW reports as not existing. Capped
    // well above any realistic drive count as a hard backstop.
    private const int MaxPhysicalDrives = 64;

    // CreateFileW's GetLastError() when no drive exists at the requested
    // index (the enumeration's actual end signal).
    private const int ErrorFileNotFound = 2;

    private static (long BytesRead, long BytesWritten) ReadCumulativeCounters()
    {
        long totalRead = 0;
        long totalWritten = 0;
        for (var i = 0; i < MaxPhysicalDrives; i++)
        {
            // Scoped per drive, mirroring NetworkRateReader's per-NIC
            // try/catch: one bad drive is skipped without discarding totals
            // already summed from drives read earlier this tick.
            try
            {
                using var handle = Native.CreateFileW(
                    $@"\\.\PhysicalDrive{i}",
                    0,
                    Native.FILE_SHARE_READ | Native.FILE_SHARE_WRITE,
                    IntPtr.Zero,
                    Native.OPEN_EXISTING,
                    0,
                    IntPtr.Zero);
                if (handle.IsInvalid)
                {
                    if (Marshal.GetLastWin32Error() == ErrorFileNotFound)
                    {
                        break;
                    }
                    continue;
                }

                if (TryReadPerformance(handle, out var read, out var written))
                {
                    totalRead += read;
                    totalWritten += written;
                }
            }
            catch
            {
                // This drive vanished mid-enumeration or failed to read;
                // skip it and keep the totals already summed.
            }
        }
        return (totalRead, totalWritten);
    }

    // DISK_PERFORMANCE (winioctl.h) is 88 bytes on x64 (five LARGE_INTEGER
    // fields, four DWORDs, another LARGE_INTEGER, a DWORD, then an 8-char
    // WCHAR name, padded to 8-byte alignment). Only the leading two
    // LARGE_INTEGER fields (BytesRead/BytesWritten) are read here, but the
    // output buffer must still be sized for the whole structure or
    // DeviceIoControl fails with an insufficient-buffer error.
    private const int DiskPerformanceBufferSize = 128;
    private const uint IoctlDiskPerformance = 0x00070020;

    private static bool TryReadPerformance(SafeFileHandle handle, out long bytesRead, out long bytesWritten)
    {
        bytesRead = 0;
        bytesWritten = 0;
        var buffer = Marshal.AllocHGlobal(DiskPerformanceBufferSize);
        try
        {
            if (!Native.DeviceIoControl(
                    handle, IoctlDiskPerformance, IntPtr.Zero, 0,
                    buffer, DiskPerformanceBufferSize, out _, IntPtr.Zero))
            {
                return false;
            }
            bytesRead = Marshal.ReadInt64(buffer, 0);
            bytesWritten = Marshal.ReadInt64(buffer, 8);
            return true;
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    private static class Native
    {
        public const uint FILE_SHARE_READ = 0x1;
        public const uint FILE_SHARE_WRITE = 0x2;
        public const uint OPEN_EXISTING = 3;

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "CreateFileW")]
        public static extern SafeFileHandle CreateFileW(
            string fileName, uint desiredAccess, uint shareMode, IntPtr securityAttributes,
            uint creationDisposition, uint flags, IntPtr templateFile);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern bool DeviceIoControl(
            SafeFileHandle device, uint ioControlCode,
            IntPtr inBuffer, uint inBufferSize,
            IntPtr outBuffer, uint outBufferSize,
            out uint bytesReturned, IntPtr overlapped);
    }
#endif
}
