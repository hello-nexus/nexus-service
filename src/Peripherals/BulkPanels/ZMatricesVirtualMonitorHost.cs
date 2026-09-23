#if WINDOWS
using System;
using System.Diagnostics;
using System.IO;
using System.IO.MemoryMappedFiles;
using System.Linq;
using System.Threading;
using Nexus.Service.Helper;
using Nexus.Service.Models.Panel;
using Nexus.Service.Panel.Streams;
using Nexus.Service.Platform;

namespace Nexus.Service.Peripherals.BulkPanels;

/// <summary>
/// Windows monitors through the ZMatrices virtual display driver, an IddCx driver the
/// ZMatrices app installs. Its helper creates the monitor as a software device that lives
/// until the parent process exits or a named stop event is set, and the driver publishes
/// every desktop frame in a named section: a 64-byte header (magic "ZMID", version 1,
/// sequence at 8, width/height/stride/size at 16-28) then top-down BGRA. The sequence is
/// odd while the driver writes.
/// </summary>
public sealed class ZMatricesVirtualMonitorHost : IVirtualMonitorHost
{
    private const string PackageDir = @"ZMatrices\resources\zmUsbSendJpg\drivers\ZmVirtualDisplay";
    private const string HelperExe = "ZmVirtualDisplayDevice.exe";
    /// <summary>EnumDisplayDevices DeviceString of the driver's adapter.</summary>
    internal const string AdapterName = "ZMatrices Virtual Display";
    private const int StartTimeoutMs = 10_000;

    private readonly HelperRegistry? _helpers;

    public ZMatricesVirtualMonitorHost(HelperRegistry? helpers)
    {
        _helpers = helpers;
    }

    public IVirtualMonitor? Create(int width, int height, string instanceKey, CancellationToken ct, out string failureState)
    {
        failureState = SecondaryMonitorStates.Failed;
        var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), PackageDir);
        var helper = Path.Combine(dir, HelperExe);
        if (!File.Exists(helper) || !IsDriverStaged())
        {
            ServiceLog.Warn($"[zm-virtual-monitor] driver not installed (helper present={File.Exists(helper)})");
            failureState = SecondaryMonitorStates.DriverMissing;
            return null;
        }

        var stopName = $"Global\\NexusVirtualMonitorStop-{instanceKey}";
        var stop = new EventWaitHandle(false, EventResetMode.ManualReset, stopName);
        Process? process = null;
        try
        {
            var logDir = Path.Combine(ServiceLog.LogsDirectory, "zm-virtual-display");
            Directory.CreateDirectory(logDir);
            var psi = new ProcessStartInfo(helper)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                WorkingDirectory = dir,
            };
            foreach (var arg in new[]
            {
                "--stay",
                "--parent-pid", Environment.ProcessId.ToString(),
                "--stop-event", stopName,
                "--width", width.ToString(),
                "--height", height.ToString(),
                "--instance-key", instanceKey,
                "--log-dir", logDir,
            })
            {
                psi.ArgumentList.Add(arg);
            }
            process = Process.Start(psi);
            if (process is null)
            {
                stop.Dispose();
                return null;
            }

            var sw = Stopwatch.StartNew();
            while (true)
            {
                ct.ThrowIfCancellationRequested();
                MemoryMappedFile? map = null;
                try
                {
                    map = MemoryMappedFile.OpenExisting($"Global\\ZmVirtualDisplayFrameV2-{instanceKey}", MemoryMappedFileRights.Read);
                    var ready = EventWaitHandle.OpenExisting($"Global\\ZmVirtualDisplayFrameReadyV2-{instanceKey}");
                    ServiceLog.Info($"[zm-virtual-monitor] {width}x{height} up in {sw.ElapsedMilliseconds} ms (key {instanceKey})");
                    return new Monitor(map, ready, stop, process, width, height, _helpers);
                }
                catch (Exception ex) when (ex is FileNotFoundException or WaitHandleCannotBeOpenedException)
                {
                    map?.Dispose();
                    if (process.HasExited || sw.ElapsedMilliseconds > StartTimeoutMs)
                    {
                        ServiceLog.Warn($"[zm-virtual-monitor] no frames section after {sw.ElapsedMilliseconds} ms (helper exited={process.HasExited})");
                        break;
                    }
                    if (ct.WaitHandle.WaitOne(100))
                    {
                        break;
                    }
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            ServiceLog.Error($"[zm-virtual-monitor] start failed: {ex.GetType().Name}: {ex.Message}");
        }
        Stop(stop, process);
        return null;
    }

    /// <summary>The driver package sits in the driver store once the ZMatrices app has installed it.</summary>
    private static bool IsDriverStaged()
    {
        var store = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "DriverStore", "FileRepository");
        try
        {
            return Directory.EnumerateDirectories(store, "zmvirtualdisplay.inf_*").Any();
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static void Stop(EventWaitHandle stop, Process? process)
    {
        try { stop.Set(); } catch (ObjectDisposedException) { }
        if (process is not null)
        {
            try
            {
                if (!process.WaitForExit(3000))
                {
                    process.Kill();
                }
            }
            catch (InvalidOperationException) { }
            process.Dispose();
        }
        stop.Dispose();
    }

    private sealed class Monitor : IVirtualMonitor
    {
        private const int HeaderBytes = 64;
        private const int Magic = 0x44494D5A;

        private readonly MemoryMappedFile _map;
        private readonly MemoryMappedViewAccessor _view;
        private readonly EventWaitHandle _ready;
        private readonly EventWaitHandle _stop;
        private readonly Process _process;
        private readonly int _width;
        private readonly int _height;
        private readonly HelperRegistry? _helpers;
        private long _lastSequence = -1;
        private int _disposed;

        public Monitor(MemoryMappedFile map, EventWaitHandle ready, EventWaitHandle stop, Process process, int width, int height, HelperRegistry? helpers)
        {
            _map = map;
            _view = map.CreateViewAccessor(0, HeaderBytes + (long)width * height * 4, MemoryMappedFileAccess.Read);
            _ready = ready;
            _stop = stop;
            _process = process;
            _width = width;
            _height = height;
            _helpers = helpers;
        }

        public bool TryReadFrame(byte[] destination, int timeoutMs, CancellationToken ct)
        {
            if (WaitHandle.WaitAny(new[] { _ready, ct.WaitHandle }, timeoutMs) == 1)
            {
                ct.ThrowIfCancellationRequested();
            }
            int bytes = _width * _height * 4;
            for (int attempt = 0; attempt < 5; attempt++)
            {
                long sequence = _view.ReadInt64(8);
                if ((sequence & 1) != 0)
                {
                    Thread.SpinWait(100);
                    continue;
                }
                if (sequence == _lastSequence)
                {
                    return false;
                }
                if (_view.ReadInt32(0) != Magic
                    || _view.ReadInt32(16) != _width
                    || _view.ReadInt32(20) != _height
                    || _view.ReadInt32(28) != bytes)
                {
                    return false;
                }
                _view.ReadArray(HeaderBytes, destination, 0, bytes);
                Thread.MemoryBarrier();
                if (_view.ReadInt64(8) == sequence)
                {
                    _lastSequence = sequence;
                    return true;
                }
            }
            return false;
        }

        public bool InjectTouch(uint pointerId, TouchPhase phase, int x, int y)
        {
            if (_helpers is null)
            {
                return false;
            }
            return Nexus.Service.Helper.Domains.InputCommands.InjectTouch(_helpers, new TouchInjectBody
            {
                Adapter = AdapterName,
                PointerId = pointerId,
                Phase = phase switch
                {
                    TouchPhase.Down => "down",
                    TouchPhase.Move => "move",
                    _ => "up",
                },
                X = x,
                Y = y,
            });
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 1)
            {
                return;
            }
            _view.Dispose();
            _map.Dispose();
            _ready.Dispose();
            Stop(_stop, _process);
            ServiceLog.Info("[zm-virtual-monitor] removed");
        }
    }
}
#endif
