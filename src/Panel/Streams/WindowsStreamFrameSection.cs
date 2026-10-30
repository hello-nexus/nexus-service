using System;
using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Principal;
using System.Threading;
using Nexus.Service.Platform;

namespace Nexus.Service.Panel.Streams;

/// <summary>
/// Service-owned Global section plus frame-ready event for one stream writer, with a reader
/// thread that hands every new frame to a callback. The service runs as SYSTEM in session 0 and
/// the overlay in the user session, so the objects are created here with a DACL naming SYSTEM
/// and the overlay's user only. Direct CreateFileMappingW because the managed MemoryMappedFile
/// API lost named-object security descriptors in .NET Core.
/// </summary>
[SupportedOSPlatform("windows")]
internal sealed unsafe partial class WindowsStreamFrameSection : IDisposable
{
    private readonly nint _mapping;
    private readonly byte* _view;
    private readonly nint _ready;
    private readonly nint _stop;
    private readonly long _capacity;
    private readonly int _maxFrameBytes;
    private readonly Action<byte[], int> _onFrame;
    private readonly Thread _thread;
    private volatile bool _disposed;
    private long _lastSequence;

    public string Name { get; }

    /// <summary>
    /// SID of the user the overlay runs as: the console session's user when this process is
    /// SYSTEM, otherwise the process user. Null when nobody can be resolved.
    /// </summary>
    public static string? ResolveOverlayUserSid()
    {
        try
        {
            using var self = WindowsIdentity.GetCurrent();
            if (!self.IsSystem)
            {
                return self.User?.Value;
            }
            var session = WTSGetActiveConsoleSessionId();
            if (session == 0xFFFFFFFF || !WTSQueryUserToken(session, out var raw) || raw == 0)
            {
                return null;
            }
            using var token = new Microsoft.Win32.SafeHandles.SafeAccessTokenHandle(raw);
            using var identity = new WindowsIdentity(token.DangerousGetHandle());
            return identity.User?.Value;
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or ArgumentException)
        {
            return null;
        }
    }

    /// <param name="onFrame">Gets a pooled array and the frame length in it; ownership of the array passes to the callback.</param>
    private WindowsStreamFrameSection(string name, nint mapping, byte* view, nint ready, nint stop, int frameBytes, Action<byte[], int> onFrame)
    {
        Name = name;
        _mapping = mapping;
        _view = view;
        _ready = ready;
        _stop = stop;
        _maxFrameBytes = frameBytes;
        _capacity = StreamFrameSection.SectionBytes(frameBytes);
        _onFrame = onFrame;
        StreamFrameSection.Initialize(view);
        _thread = new Thread(Run) { IsBackground = true, Name = "stream-shm-" + name };
        _thread.Start();
    }

    public static WindowsStreamFrameSection Create(string name, string sddl, int frameBytes, Action<byte[], int> onFrame)
    {
        var total = StreamFrameSection.SectionBytes(frameBytes);
        if (!ConvertStringSecurityDescriptorToSecurityDescriptorW(sddl, 1, out var descriptor, out _))
            throw new Win32Exception(Marshal.GetLastPInvokeError(), "invalid stream section SDDL");
        try
        {
            var attributes = new SecurityAttributes
            {
                Length = (uint)Unsafe.SizeOf<SecurityAttributes>(),
                SecurityDescriptor = descriptor,
                InheritHandle = 0,
            };
            var mapping = CreateFileMappingW(-1, in attributes, 0x04, (uint)(total >> 32), (uint)total, name);
            var error = Marshal.GetLastPInvokeError();
            if (mapping == 0)
                throw new Win32Exception(error, "failed to create the stream section");
            if (error == ErrorAlreadyExists)
            {
                // The name exists, so our size and DACL were ignored.
                CloseHandle(mapping);
                throw new IOException("stream section name already in use");
            }
            var view = MapViewOfFile(mapping, 0x000F001F, 0, 0, 0);
            if (view == 0)
            {
                var viewError = Marshal.GetLastPInvokeError();
                CloseHandle(mapping);
                throw new Win32Exception(viewError, "failed to map the stream section");
            }
            var ready = CreateEventW(in attributes, 0, 0, StreamFrameSection.ReadyName(name));
            var eventError = Marshal.GetLastPInvokeError();
            if (ready == 0 || eventError == ErrorAlreadyExists)
            {
                if (ready != 0)
                {
                    CloseHandle(ready);
                }
                UnmapViewOfFile(view);
                CloseHandle(mapping);
                throw ready == 0
                    ? new Win32Exception(eventError, "failed to create the stream section event")
                    : new IOException("stream section event name already in use");
            }
            // Service-only: the overlay user holds write access to the ready event, so Dispose cannot rely on it to wake the reader.
            var local = new SecurityAttributes { Length = (uint)Unsafe.SizeOf<SecurityAttributes>() };
            var stop = CreateEventW(in local, 1, 0, null!);
            if (stop == 0)
            {
                var stopError = Marshal.GetLastPInvokeError();
                CloseHandle(ready);
                UnmapViewOfFile(view);
                CloseHandle(mapping);
                throw new Win32Exception(stopError, "failed to create the stream section stop event");
            }
            return new WindowsStreamFrameSection(name, mapping, (byte*)view, ready, stop, frameBytes, onFrame);
        }
        finally
        {
            LocalFree(descriptor);
        }
    }

    private void Run()
    {
        var handles = stackalloc nint[2] { _ready, _stop };
        while (!_disposed)
        {
            WaitForMultipleObjects(2, handles, 0, Infinite);
            if (_disposed)
            {
                break;
            }
            if (!StreamFrameSection.HasNew(_view, _lastSequence))
            {
                continue;
            }
            var buffer = StreamFrame.Pool.Rent(_maxFrameBytes);
            try
            {
                if (StreamFrameSection.TryRead(_view, _capacity, ref _lastSequence, buffer, out var length))
                {
                    StreamFrameSection.PublishConsumed(_view, _lastSequence);
                    var handedOver = buffer;
                    buffer = null!;
                    _onFrame(handedOver, length);
                }
            }
            catch (Exception ex)
            {
                ServiceLog.Warn($"[streamed-panel] shared frame read failed: {ex.GetType().Name}: {ex.Message}");
            }
            finally
            {
                if (buffer is not null)
                {
                    StreamFrame.Pool.Return(buffer);
                }
            }
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;
        SetEvent(_stop);
        if (Thread.CurrentThread != _thread && !_thread.Join(2000))
        {
            // The reader outlived the join (a callback still running): leak the mapping rather than unmap under it.
            return;
        }
        UnmapViewOfFile((nint)_view);
        CloseHandle(_mapping);
        CloseHandle(_ready);
        CloseHandle(_stop);
    }

    private const int ErrorAlreadyExists = 183;
    private const uint Infinite = 0xFFFFFFFF;

    [StructLayout(LayoutKind.Sequential)]
    private struct SecurityAttributes
    {
        public uint Length;
        public nint SecurityDescriptor;
        public int InheritHandle;
    }

    [LibraryImport("advapi32.dll", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool ConvertStringSecurityDescriptorToSecurityDescriptorW(
        string sddl, uint revision, out nint descriptor, out uint descriptorBytes);

    [LibraryImport("kernel32.dll", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    private static partial nint CreateFileMappingW(
        nint file, in SecurityAttributes attributes, uint protect, uint maximumSizeHigh, uint maximumSizeLow, string name);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static partial nint MapViewOfFile(nint mapping, uint desiredAccess, uint offsetHigh, uint offsetLow, nuint bytesToMap);

    [LibraryImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool UnmapViewOfFile(nint baseAddress);

    [LibraryImport("kernel32.dll", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    private static partial nint CreateEventW(in SecurityAttributes attributes, int manualReset, int initialState, string name);

    [LibraryImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetEvent(nint handle);

    [LibraryImport("kernel32.dll")]
    private static partial uint WaitForMultipleObjects(uint count, nint* handles, int waitAll, uint milliseconds);

    [LibraryImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CloseHandle(nint handle);

    [LibraryImport("kernel32.dll")]
    private static partial nint LocalFree(nint memory);

    [LibraryImport("kernel32.dll")]
    private static partial uint WTSGetActiveConsoleSessionId();

    [LibraryImport("wtsapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool WTSQueryUserToken(uint sessionId, out nint token);
}
