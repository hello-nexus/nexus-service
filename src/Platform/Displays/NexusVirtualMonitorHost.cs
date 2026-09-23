#if WINDOWS
using System;
using System.Diagnostics;
using System.IO;
using System.IO.MemoryMappedFiles;
using System.Linq;
using System.Threading;
using Microsoft.Win32;
using Nexus.Service.Helper;
using Nexus.Service.Models.Panel;
using Nexus.Service.Panel.Streams;
using Nexus.Service.Peripherals.BulkPanels;

namespace Nexus.Service.Platform.Displays;

/// <summary>
/// Windows monitors through the Nexus Virtual Display, the IddCx driver shipped in {install}/vdd
/// (source in Bundled/windows/nexus-vdd). The native host creates the monitor as a software
/// device that lives until this process exits or the stop event is set; the driver publishes
/// each desktop frame in Global\NexusVirtualDisplayFrame: a 64-byte header (magic "NXVD",
/// version 1, sequence at 8, width/height/stride/size at 16-28) then top-down BGRA. The
/// sequence is odd while the driver writes.
/// </summary>
public sealed class NexusVirtualMonitorHost : IVirtualMonitorHost
{
    /// <summary>EnumDisplayDevices DeviceString of the driver's adapter.</summary>
    internal const string AdapterName = "Nexus Virtual Display";
    private const string StopEventName = @"Global\NexusVirtualDisplayStop";
    private const int StartTimeoutMs = 15_000;

    private readonly HelperRegistry? _helpers;
    private readonly string _packageDir = Path.Combine(AppContext.BaseDirectory, "vdd");
    private int _installAttempted;
    // One device instance and one stop event exist system-wide, so a start waits for the
    // previous monitor to be fully gone.
    private readonly object _gate = new();

    public NexusVirtualMonitorHost(HelperRegistry? helpers)
    {
        _helpers = helpers;
    }

    public bool IsAvailable =>
        File.Exists(Path.Combine(_packageDir, "NexusVirtualDisplay.inf"))
        && File.Exists(Path.Combine(_packageDir, "NexusVirtualDisplayHost.exe"));

    public IVirtualMonitor? Create(int width, int height, CancellationToken ct, out string failureState)
    {
        lock (_gate)
        {
            return CreateLocked(width, height, ct, out failureState);
        }
    }

    private IVirtualMonitor? CreateLocked(int width, int height, CancellationToken ct, out string failureState)
    {
        failureState = SecondaryMonitorStates.Failed;
        var inf = Path.Combine(_packageDir, "NexusVirtualDisplay.inf");
        var host = Path.Combine(_packageDir, "NexusVirtualDisplayHost.exe");
        if (!IsAvailable || !EnsureDriver(inf))
        {
            failureState = SecondaryMonitorStates.DriverMissing;
            return null;
        }

        EventWaitHandle? stop = null;
        Process? process = null;
        try
        {
            // The driver reads the size when the device starts.
            using (var key = Registry.LocalMachine.CreateSubKey(@"SOFTWARE\Nexus\VirtualDisplay"))
            {
                key.SetValue("Width", width, RegistryValueKind.DWord);
                key.SetValue("Height", height, RegistryValueKind.DWord);
            }

            // Named events outlive a handle another start still holds, so clear it explicitly.
            stop = new EventWaitHandle(false, EventResetMode.ManualReset, StopEventName);
            stop.Reset();

            var psi = new ProcessStartInfo(host)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                WorkingDirectory = _packageDir,
            };
            psi.ArgumentList.Add("--parent-pid");
            psi.ArgumentList.Add(Environment.ProcessId.ToString());
            psi.ArgumentList.Add("--stop-event");
            psi.ArgumentList.Add(StopEventName);
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
                    map = MemoryMappedFile.OpenExisting(FrameSection.SectionName, MemoryMappedFileRights.Read);
                    var ready = EventWaitHandle.OpenExisting(FrameSection.ReadyName);
                    ServiceLog.Info($"[virtual-monitor] {width}x{height} up in {sw.ElapsedMilliseconds} ms");
                    return new Monitor(this, new FrameSection(map, ready, width, height), stop, process, _helpers);
                }
                catch (Exception ex) when (ex is FileNotFoundException or WaitHandleCannotBeOpenedException)
                {
                    map?.Dispose();
                    if (process.HasExited || sw.ElapsedMilliseconds > StartTimeoutMs)
                    {
                        ServiceLog.Warn($"[virtual-monitor] no frames after {sw.ElapsedMilliseconds} ms (host exited={process.HasExited}{(process.HasExited ? $", code {process.ExitCode}" : "")})");
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
            ServiceLog.Error($"[virtual-monitor] start failed: {ex.GetType().Name}: {ex.Message}");
        }
        Stop(stop, process);
        return null;
    }

    private void StopMonitor(EventWaitHandle stop, Process process)
    {
        lock (_gate)
        {
            Stop(stop, process);
            // The next start must not open this monitor's section while its driver unloads.
            for (int i = 0; i < 30 && SectionExists(); i++)
            {
                Thread.Sleep(100);
            }
        }
    }

    private static bool SectionExists()
    {
        try
        {
            using var map = MemoryMappedFile.OpenExisting(FrameSection.SectionName, MemoryMappedFileRights.Read);
            return true;
        }
        catch (Exception ex) when (ex is FileNotFoundException or UnauthorizedAccessException or IOException)
        {
            return false;
        }
    }

    /// <summary>
    /// Adds the bundled package once per service run: pnputil installs it when it is new or newer
    /// and leaves an identical one alone. It succeeds silently only once the signing publisher is
    /// trusted on this PC.
    /// </summary>
    private bool EnsureDriver(string inf)
    {
        if (Interlocked.Exchange(ref _installAttempted, 1) == 0)
        {
            try
            {
                var psi = new ProcessStartInfo("pnputil.exe")
                {
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                };
                psi.ArgumentList.Add("/add-driver");
                psi.ArgumentList.Add(inf);
                psi.ArgumentList.Add("/install");
                using var pnputil = Process.Start(psi);
                if (pnputil is not null)
                {
                    var output = pnputil.StandardOutput.ReadToEndAsync();
                    if (!pnputil.WaitForExit(30_000))
                    {
                        pnputil.Kill();
                        ServiceLog.Warn("[virtual-monitor] driver package add timed out");
                    }
                    else
                    {
                        var summary = (output.Wait(1000) ? output.Result : "").Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0).LastOrDefault();
                        ServiceLog.Info($"[virtual-monitor] driver package add exit={pnputil.ExitCode}: {summary}");
                    }
                }
            }
            catch (Exception ex)
            {
                ServiceLog.Warn($"[virtual-monitor] driver package add failed: {ex.Message}");
            }
        }
        return IsDriverStaged();
    }

    private static bool IsDriverStaged()
    {
        var store = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "DriverStore", "FileRepository");
        try
        {
            return Directory.EnumerateDirectories(store, "nexusvirtualdisplay.inf_*").Any();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static void Stop(EventWaitHandle? stop, Process? process)
    {
        try { stop?.Set(); } catch (ObjectDisposedException) { }
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
        stop?.Dispose();
    }

    private sealed class FrameSection : IDisposable
    {
        internal const string SectionName = @"Global\NexusVirtualDisplayFrame";
        internal const string ReadyName = @"Global\NexusVirtualDisplayFrameReady";
        private const int HeaderBytes = 64;
        private const int Magic = 0x4456584E;

        private readonly MemoryMappedFile _map;
        private readonly MemoryMappedViewAccessor _view;
        private readonly EventWaitHandle _ready;
        private readonly int _width;
        private readonly int _height;
        private long _lastSequence = -1;

        public FrameSection(MemoryMappedFile map, EventWaitHandle ready, int width, int height)
        {
            _map = map;
            _view = map.CreateViewAccessor(0, HeaderBytes + (long)width * height * 4, MemoryMappedFileAccess.Read);
            _ready = ready;
            _width = width;
            _height = height;
        }

        public bool TryRead(byte[] destination, int timeoutMs, CancellationToken ct)
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
                // A desktop resized under us is skipped rather than streamed at the wrong size.
                if (sequence == _lastSequence
                    || _view.ReadInt32(0) != Magic
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

        public void Dispose()
        {
            _view.Dispose();
            _map.Dispose();
            _ready.Dispose();
        }
    }

    private sealed class Monitor : IVirtualMonitor
    {
        private readonly NexusVirtualMonitorHost _owner;
        private readonly FrameSection _frames;
        private readonly EventWaitHandle _stop;
        private readonly Process _process;
        private readonly HelperRegistry? _helpers;
        private int _disposed;

        public Monitor(NexusVirtualMonitorHost owner, FrameSection frames, EventWaitHandle stop, Process process, HelperRegistry? helpers)
        {
            _owner = owner;
            _frames = frames;
            _stop = stop;
            _process = process;
            _helpers = helpers;
        }

        public bool TryReadFrame(byte[] destination, int timeoutMs, CancellationToken ct) =>
            _frames.TryRead(destination, timeoutMs, ct);

        public bool IsAlive
        {
            get
            {
                try { return !_process.HasExited; }
                catch (InvalidOperationException) { return false; }
            }
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
            _frames.Dispose();
            _owner.StopMonitor(_stop, _process);
            ServiceLog.Info("[virtual-monitor] removed");
        }
    }
}
#endif
