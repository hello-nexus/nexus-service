using System;
using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Threading;
using Nexus.Service.Platform;

namespace Nexus.Service.Panel.Streams;

/// <summary>
/// Service-owned Global section plus frame-ready event for one stream session, with a reader
/// thread that hands every new frame to a callback. The service runs as SYSTEM in session 0 and
/// the overlay in the user session, so the objects are created here with a DACL that lets the
/// interactive user write. Direct CreateFileMappingW because the managed MemoryMappedFile API
/// lost named-object security descriptors in .NET Core.
/// </summary>
[SupportedOSPlatform("windows")]
internal sealed unsafe partial class WindowsStreamFrameSection : IDisposable
{
    private const string Sddl = "D:(A;;GA;;;SY)(A;;GA;;;BA)(A;;GRGW;;;IU)";
    private const int WaitMs = 250;

    private readonly nint _mapping;
    private readonly byte* _view;
    private readonly nint _ready;
    private readonly long _capacity;
    private readonly int _maxFrameBytes;
    private readonly Action<byte[], int> _onFrame;
    private readonly Thread _thread;
    private volatile bool _disposed;
    private long _lastSequence;

    public string Name { get; }
    public string ReadyName => ReadyNameFor(Name);
    public int FrameBytes => _maxFrameBytes;

    public static string NameFor(string sessionId) => @"Global\NexusPanelStream-" + sessionId;
    public static string ReadyNameFor(string sectionName) => sectionName + "-Ready";

    /// <param name="onFrame">Gets a pooled array and the frame length in it; ownership of the array passes to the callback.</param>
    private WindowsStreamFrameSection(string name, nint mapping, byte* view, nint ready, int frameBytes, Action<byte[], int> onFrame)
    {
        Name = name;
        _mapping = mapping;
        _view = view;
        _ready = ready;
        _maxFrameBytes = frameBytes;
        _capacity = StreamFrameSection.SectionBytes(frameBytes);
        _onFrame = onFrame;
        StreamFrameSection.Initialize(view);
        _thread = new Thread(Run) { IsBackground = true, Name = "stream-shm-" + name };
        _thread.Start();
    }

    public static WindowsStreamFrameSection Create(string sessionId, int frameBytes, Action<byte[], int> onFrame)
    {
        var name = NameFor(sessionId);
        var total = StreamFrameSection.SectionBytes(frameBytes);
        if (!ConvertStringSecurityDescriptorToSecurityDescriptorW(Sddl, 1, out var descriptor, out _))
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
            if (error == 183)
            {
                // A section of this name already exists, so our size and DACL were ignored.
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
            var ready = CreateEventW(in attributes, 0, 0, ReadyNameFor(name));
            if (ready == 0)
            {
                var eventError = Marshal.GetLastPInvokeError();
                UnmapViewOfFile(view);
                CloseHandle(mapping);
                throw new Win32Exception(eventError, "failed to create the stream section event");
            }
            return new WindowsStreamFrameSection(name, mapping, (byte*)view, ready, frameBytes, onFrame);
        }
        finally
        {
            LocalFree(descriptor);
        }
    }

    private void Run()
    {
        while (!_disposed)
        {
            WaitForSingleObject(_ready, WaitMs);
            if (_disposed)
            {
                break;
            }
            var buffer = StreamFrame.Pool.Rent(_maxFrameBytes);
            try
            {
                if (StreamFrameSection.TryRead(_view, _capacity, ref _lastSequence, buffer, out var length))
                {
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
        SetEvent(_ready);
        if (Thread.CurrentThread != _thread && !_thread.Join(2000))
        {
            // The reader outlived the join (a callback still running): leak the mapping rather than unmap under it.
            return;
        }
        UnmapViewOfFile((nint)_view);
        CloseHandle(_mapping);
        CloseHandle(_ready);
    }

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
    private static partial uint WaitForSingleObject(nint handle, uint milliseconds);

    [LibraryImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CloseHandle(nint handle);

    [LibraryImport("kernel32.dll")]
    private static partial nint LocalFree(nint memory);
}
