#if MACOS
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Nexus.Service.Platform;

namespace Nexus.Service.Peripherals.BulkPanels;

/// <summary>
/// Bulk pipe pair over IOUSBHost.framework. No kernel driver claims these vendor-class
/// panels, so any process may configure one and open its interface, with no entitlement.
/// </summary>
public sealed unsafe class MacBulkUsbPipe : IBulkUsbPipe
{
    private const double TransferTimeoutSeconds = 5;

    private readonly uint _interfaceService;
    private readonly IntPtr _interface;
    private readonly byte _writePipeId;
    private readonly byte _readPipeId;
    private readonly Dictionary<byte, IntPtr> _pipes = new();
    private readonly object _ioLock = new();
    // Reads hold their own lock, as on Windows, so a read parked on a quiet IN pipe never
    // holds up frame writes.
    private readonly object _readLock = new();
    // Surplus of a read longer than the caller's buffer; _readLock-confined.
    private byte[] _leftover = Array.Empty<byte>();
    private int _leftoverOffset;
    private int _leftoverCount;
    private bool _disposed;

    public int ProductId { get; }

    /// <summary>A replug or firmware reboot terminates the interface, which leaves the IOService plane.</summary>
    public bool IsGone => !_disposed && MacUsbHost.IORegistryEntryInPlane(_interfaceService, "IOService") == 0;

    private MacBulkUsbPipe(uint interfaceService, IntPtr iface, IntPtr writePipe, byte writePipeId, byte readPipeId, int productId)
    {
        _interfaceService = interfaceService;
        _interface = iface;
        _writePipeId = writePipeId;
        _readPipeId = readPipeId;
        _pipes[writePipeId] = writePipe;
        ProductId = productId;
    }

    /// <summary>
    /// Opens <paramref name="interfaceService"/> and its write pipe, or returns null with the
    /// reason. Takes ownership of the service reference only on success.
    /// </summary>
    internal static MacBulkUsbPipe? TryOpen(uint interfaceService, byte writePipeId, byte readPipeId, int productId, out string? failure)
    {
        var pool = MacUsbHost.objc_autoreleasePoolPush();
        try
        {
            IntPtr error = IntPtr.Zero;
            var iface = MacUsbHost.InitWithService(MacUsbHost.InterfaceClass, interfaceService, &error);
            if (iface == IntPtr.Zero)
            {
                failure = $"interface open failed: {MacUsbHost.Describe(error)}";
                return null;
            }
            var writePipe = MacUsbHost.CopyPipe(iface, writePipeId, &error);
            if (writePipe == IntPtr.Zero)
            {
                failure = $"no pipe 0x{writePipeId:X2}: {MacUsbHost.Describe(error)}";
                MacUsbHost.DestroyAndRelease(iface);
                return null;
            }
            var pipe = new MacBulkUsbPipe(interfaceService, iface, writePipe, writePipeId, readPipeId, productId);
            // Clears a halt a previous owner may have left, as the WinUSB pipe does on open.
            MacUsbHost.ClearStall(writePipe);
            if (readPipeId != 0)
            {
                var readPipe = pipe.PipeFor(readPipeId);
                if (readPipe != IntPtr.Zero)
                {
                    MacUsbHost.ClearStall(readPipe);
                }
            }
            failure = null;
            return pipe;
        }
        finally
        {
            MacUsbHost.objc_autoreleasePoolPop(pool);
        }
    }

    public bool Write(ReadOnlySpan<byte> data) => Write(_writePipeId, data);

    public bool Write(byte pipeId, ReadOnlySpan<byte> data) => WriteCounted(pipeId, data) == data.Length;

    /// <summary>Bytes the device took, also when the transfer failed part way and IOUSBHost reports a count; -1 when it reports none.</summary>
    public int WriteCounted(byte pipeId, ReadOnlySpan<byte> data)
    {
        if (_disposed || data.Length == 0)
        {
            return -1;
        }
        lock (_ioLock)
        {
            if (_disposed)
            {
                return -1;
            }
            // Pushed before PipeFor: a failed pipe copy leaves an autoreleased NSError.
            var pool = MacUsbHost.objc_autoreleasePoolPush();
            try
            {
                var pipe = PipeFor(pipeId);
                if (pipe == IntPtr.Zero)
                {
                    return -1;
                }
                IntPtr nsData;
                fixed (byte* p = data)
                {
                    nsData = MacUsbHost.NewData(p, data.Length);
                }
                try
                {
                    int moved = MacUsbHost.Transfer(pipe, nsData, TransferTimeoutSeconds, out _, out int partial);
                    return moved >= 0 ? moved : partial > 0 ? partial : -1;
                }
                finally
                {
                    MacUsbHost.Release(nsData);
                }
            }
            finally
            {
                MacUsbHost.objc_autoreleasePoolPop(pool);
            }
        }
    }

    public bool ClearHalt(byte pipeId)
    {
        lock (_ioLock)
        {
            if (_disposed)
            {
                return false;
            }
            var pool = MacUsbHost.objc_autoreleasePoolPush();
            try
            {
                var pipe = PipeFor(pipeId);
                if (pipe == IntPtr.Zero)
                {
                    return false;
                }
                MacUsbHost.ClearStall(pipe);
                return true;
            }
            finally
            {
                MacUsbHost.objc_autoreleasePoolPop(pool);
            }
        }
    }

    public int Read(Span<byte> buffer, int timeoutMs) => Read(_readPipeId, buffer, timeoutMs);

    public int Read(byte pipeId, Span<byte> buffer, int timeoutMs)
    {
        if (_disposed || pipeId == 0 || buffer.Length == 0)
        {
            return -1;
        }
        lock (_readLock)
        {
            if (_disposed)
            {
                return -1;
            }
            if (pipeId == _readPipeId && _leftoverCount > 0)
            {
                return TakeLeftover(buffer);
            }
            var pool = MacUsbHost.objc_autoreleasePoolPush();
            IntPtr nsData = IntPtr.Zero;
            try
            {
                var pipe = PipeFor(pipeId);
                if (pipe == IntPtr.Zero)
                {
                    return -1;
                }
                // IOUSBHost fails a request shorter than the packet the device sends (overrun),
                // so ask for whole packets and keep any surplus for the next read, as WinUSB's
                // partial reads do.
                int packet = MacUsbHost.MaxPacketSize(pipe);
                int request = (buffer.Length + packet - 1) / packet * packet;
                nsData = MacUsbHost.NewData(null, request);
                var read = MacUsbHost.Transfer(pipe, nsData, Math.Max(1, timeoutMs) / 1000.0, out var timedOut);
                if (read < 0)
                {
                    // A timed-out read is a quiet panel, not a broken one.
                    return timedOut ? 0 : -1;
                }
                var received = new ReadOnlySpan<byte>((void*)MacUsbHost.Bytes(nsData), read);
                if (read <= buffer.Length)
                {
                    received.CopyTo(buffer);
                    return read;
                }
                received[..buffer.Length].CopyTo(buffer);
                if (pipeId != _readPipeId)
                {
                    return buffer.Length;
                }
                _leftover = received[buffer.Length..].ToArray();
                _leftoverOffset = 0;
                _leftoverCount = _leftover.Length;
                return buffer.Length;
            }
            finally
            {
                MacUsbHost.Release(nsData);
                MacUsbHost.objc_autoreleasePoolPop(pool);
            }
        }
    }

    private int TakeLeftover(Span<byte> buffer)
    {
        int n = Math.Min(buffer.Length, _leftoverCount);
        _leftover.AsSpan(_leftoverOffset, n).CopyTo(buffer);
        _leftoverOffset += n;
        _leftoverCount -= n;
        return n;
    }

    /// <summary>The pipe for an endpoint address, copied from the interface on first use.</summary>
    private IntPtr PipeFor(byte pipeId)
    {
        lock (_pipes)
        {
            if (_pipes.TryGetValue(pipeId, out var pipe))
            {
                return pipe;
            }
            IntPtr error = IntPtr.Zero;
            pipe = MacUsbHost.CopyPipe(_interface, pipeId, &error);
            if (pipe != IntPtr.Zero)
            {
                _pipes[pipeId] = pipe;
            }
            return pipe;
        }
    }

    public void Dispose()
    {
        lock (_ioLock)
        lock (_readLock)
        {
            if (_disposed)
            {
                return;
            }
            _disposed = true;
            var pool = MacUsbHost.objc_autoreleasePoolPush();
            try
            {
                lock (_pipes)
                {
                    foreach (var pipe in _pipes.Values)
                    {
                        MacUsbHost.Release(pipe);
                    }
                    _pipes.Clear();
                }
                MacUsbHost.DestroyAndRelease(_interface);
            }
            finally
            {
                MacUsbHost.objc_autoreleasePoolPop(pool);
            }
            MacUsbHost.IOObjectRelease(_interfaceService);
        }
    }
}

public sealed unsafe class MacBulkUsbPipeFactory : IBulkUsbPipeFactory
{
    /// <summary>How long a freshly configured device gets to publish its interfaces.</summary>
    private const uint InterfaceAppearTimeoutSeconds = 2;

    public IBulkUsbPipe? Open(int vendorId, int productId, byte writePipeId, byte readPipeId)
    {
        if (!MacUsbHost.EnsureLoaded())
        {
            return null;
        }
        var failures = new List<string>();
        foreach (var device in MacUsbHost.FindDevices(vendorId, productId))
        {
            try
            {
                var interfaces = MacUsbHost.ChildInterfaces(device);
                if (interfaces.Count == 0)
                {
                    if (!Configure(device, out var reason))
                    {
                        failures.Add(reason);
                        continue;
                    }
                    interfaces = WaitForInterfaces(device);
                    if (interfaces.Count == 0)
                    {
                        failures.Add("configured, but no interface appeared");
                        continue;
                    }
                }
                MacBulkUsbPipe? opened = null;
                foreach (var service in interfaces)
                {
                    if (opened is null)
                    {
                        opened = MacBulkUsbPipe.TryOpen(service, writePipeId, readPipeId, productId, out var failure);
                        if (opened is not null)
                        {
                            continue;
                        }
                        failures.Add(failure!);
                    }
                    MacUsbHost.IOObjectRelease(service);
                }
                if (opened is not null)
                {
                    return opened;
                }
            }
            finally
            {
                MacUsbHost.IOObjectRelease(device);
            }
        }
        if (failures.Count > 0)
        {
            ServiceLog.Info($"[bulk-panel] {vendorId:X4}:{productId:X4} is present but could not be opened: {string.Join("; ", failures)}");
        }
        return null;
    }

    /// <summary>
    /// Selects the device's first configuration. The device is held open only for this:
    /// the configuration outlives the handle, and the pipes need just the interface.
    /// </summary>
    private static bool Configure(uint device, out string reason)
    {
        var pool = MacUsbHost.objc_autoreleasePoolPush();
        try
        {
            IntPtr error = IntPtr.Zero;
            var host = MacUsbHost.InitWithService(MacUsbHost.DeviceClass, device, &error);
            if (host == IntPtr.Zero)
            {
                reason = $"device open failed: {MacUsbHost.Describe(error)}";
                return false;
            }
            try
            {
                var descriptor = MacUsbHost.ConfigurationDescriptorAt(host, 0, &error);
                if (descriptor == IntPtr.Zero)
                {
                    reason = $"no configuration descriptor: {MacUsbHost.Describe(error)}";
                    return false;
                }
                // bConfigurationValue, per the USB configuration descriptor layout.
                var value = ((byte*)descriptor)[5];
                if (!MacUsbHost.Configure(host, value, &error))
                {
                    reason = $"configure {value} failed: {MacUsbHost.Describe(error)}";
                    return false;
                }
                reason = "";
                return true;
            }
            finally
            {
                MacUsbHost.DestroyAndRelease(host);
            }
        }
        finally
        {
            MacUsbHost.objc_autoreleasePoolPop(pool);
        }
    }

    /// <summary>The interface nubs register asynchronously after a configuration change; matching on the device's subtree goes quiet once they have.</summary>
    private static List<uint> WaitForInterfaces(uint device)
    {
        MacUsbHost.WaitQuiet(device, InterfaceAppearTimeoutSeconds);
        return MacUsbHost.ChildInterfaces(device);
    }
}

/// <summary>IOKit, CoreFoundation and objc runtime calls for <see cref="MacBulkUsbPipe"/>. Plain scalar/pointer signatures, AOT-safe.</summary>
internal static unsafe class MacUsbHost
{
    private const string Libobjc = "/usr/lib/libobjc.dylib";
    private const string IOKit = "/System/Library/Frameworks/IOKit.framework/IOKit";
    private const string CoreFoundation = "/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation";
    private const string IOUSBHostFramework = "/System/Library/Frameworks/IOUSBHost.framework/IOUSBHost";

    private const uint KCfStringEncodingUtf8 = 0x08000100;
    private const int KCfNumberSInt32Type = 3;
    private const int RtldNow = 0x2;
    /// <summary>High-speed bulk packet size, used when a pipe reports no endpoint descriptor.</summary>
    private const int FallbackPacketSize = 512;

    // IOReturn codes a timed-out transfer completes with.
    private const int KIOReturnTimeout = unchecked((int)0xE00002D6);
    private const int KIOUSBTransactionTimeout = unchecked((int)0xE0004051);

    private static readonly object LoadLock = new();
    private static bool? _loaded;

    public static IntPtr DeviceClass { get; private set; }
    public static IntPtr InterfaceClass { get; private set; }
    private static IntPtr _dataClass;

    private static readonly IntPtr SelAlloc = Sel("alloc");
    private static readonly IntPtr SelRelease = Sel("release");
    private static readonly IntPtr SelDestroy = Sel("destroy");
    private static readonly IntPtr SelInitWithService = Sel("initWithIOService:options:queue:error:interestHandler:");
    private static readonly IntPtr SelConfigDescriptorAt = Sel("configurationDescriptorWithIndex:error:");
    private static readonly IntPtr SelConfigure = Sel("configureWithValue:matchInterfaces:error:");
    private static readonly IntPtr SelCopyPipe = Sel("copyPipeWithAddress:error:");
    private static readonly IntPtr SelClearStall = Sel("clearStallWithError:");
    private static readonly IntPtr SelSendRequest = Sel("sendIORequestWithData:bytesTransferred:completionTimeout:error:");
    private static readonly IntPtr SelInitWithBytes = Sel("initWithBytes:length:");
    private static readonly IntPtr SelInitWithLength = Sel("initWithLength:");
    private static readonly IntPtr SelMutableBytes = Sel("mutableBytes");
    private static readonly IntPtr SelDescriptors = Sel("descriptors");
    private static readonly IntPtr SelCode = Sel("code");
    private static readonly IntPtr SelDescription = Sel("localizedDescription");
    private static readonly IntPtr SelUtf8String = Sel("UTF8String");

    /// <summary>IOUSBHost's classes are not registered until the framework is loaded.</summary>
    public static bool EnsureLoaded()
    {
        lock (LoadLock)
        {
            if (_loaded is { } loaded)
            {
                return loaded;
            }
            if (dlopen(IOUSBHostFramework, RtldNow) != IntPtr.Zero)
            {
                DeviceClass = ClassGet("IOUSBHostDevice");
                InterfaceClass = ClassGet("IOUSBHostInterface");
                _dataClass = ClassGet("NSMutableData");
            }
            _loaded = DeviceClass != IntPtr.Zero && InterfaceClass != IntPtr.Zero && _dataClass != IntPtr.Zero;
            if (_loaded == false)
            {
                ServiceLog.Warn("[bulk-panel] IOUSBHost.framework failed to load; bulk-pipe panels are unavailable");
            }
            return _loaded.Value;
        }
    }

    /// <summary>Every IOUSBHostDevice with this VID/PID; the caller releases each.</summary>
    public static List<uint> FindDevices(int vendorId, int productId)
    {
        var found = new List<uint>();
        var matching = IOServiceMatching("IOUSBHostDevice");
        if (matching == IntPtr.Zero)
        {
            return found;
        }
        SetNumber(matching, "idVendor", vendorId);
        SetNumber(matching, "idProduct", productId);
        // Consumes the matching dictionary.
        if (IOServiceGetMatchingServices(0, matching, out var iterator) != 0)
        {
            return found;
        }
        uint service;
        while ((service = IOIteratorNext(iterator)) != 0)
        {
            found.Add(service);
        }
        IOObjectRelease(iterator);
        return found;
    }

    /// <summary>The device's IOUSBHostInterface children; empty while it is unconfigured. The caller releases each.</summary>
    public static List<uint> ChildInterfaces(uint device)
    {
        var found = new List<uint>();
        if (IORegistryEntryGetChildIterator(device, "IOService", out var iterator) != 0)
        {
            return found;
        }
        uint child;
        while ((child = IOIteratorNext(iterator)) != 0)
        {
            if (IOObjectConformsTo(child, "IOUSBHostInterface") != 0)
            {
                found.Add(child);
            }
            else
            {
                IOObjectRelease(child);
            }
        }
        IOObjectRelease(iterator);
        return found;
    }

    /// <summary>Default options open the service for exclusive access; a null queue makes the framework create one.</summary>
    public static IntPtr InitWithService(IntPtr cls, uint service, IntPtr* error) =>
        MsgSendInit(MsgSend(cls, SelAlloc), SelInitWithService, service, 0, IntPtr.Zero, error, IntPtr.Zero);

    public static IntPtr ConfigurationDescriptorAt(IntPtr device, nuint index, IntPtr* error) =>
        MsgSendIndexError(device, SelConfigDescriptorAt, index, error);

    public static bool Configure(IntPtr device, byte value, IntPtr* error) =>
        MsgSendConfigure(device, SelConfigure, value, 1, error) != 0;

    public static IntPtr CopyPipe(IntPtr iface, byte address, IntPtr* error) =>
        MsgSendIndexError(iface, SelCopyPipe, address, error);

    public static void ClearStall(IntPtr pipe)
    {
        IntPtr error = IntPtr.Zero;
        MsgSendError(pipe, SelClearStall, &error);
    }

    /// <summary>
    /// One synchronous transfer of the whole <paramref name="data"/> length. Returns the byte
    /// count, or -1 with <paramref name="timedOut"/> set when the deadline was what ended it.
    /// </summary>
    public static int Transfer(IntPtr pipe, IntPtr data, double timeoutSeconds, out bool timedOut) =>
        Transfer(pipe, data, timeoutSeconds, out timedOut, out _);

    /// <param name="partial">On failure, the byte count IOUSBHost reported for the part that went through.</param>
    public static int Transfer(IntPtr pipe, IntPtr data, double timeoutSeconds, out bool timedOut, out int partial)
    {
        nuint transferred = 0;
        IntPtr error = IntPtr.Zero;
        if (MsgSendTransfer(pipe, SelSendRequest, data, &transferred, timeoutSeconds, &error) != 0)
        {
            timedOut = false;
            partial = 0;
            return (int)transferred;
        }
        var code = error == IntPtr.Zero ? 0 : (int)MsgSendLong(error, SelCode);
        timedOut = code is KIOReturnTimeout or KIOUSBTransactionTimeout;
        partial = (int)transferred;
        return -1;
    }

    /// <summary>A retained NSMutableData: a copy of <paramref name="bytes"/>, or zeroed when it is null.</summary>
    public static IntPtr NewData(byte* bytes, int length)
    {
        var alloc = MsgSend(_dataClass, SelAlloc);
        return bytes is null
            ? MsgSendIndex(alloc, SelInitWithLength, (nuint)length)
            : MsgSendBytes(alloc, SelInitWithBytes, bytes, (nuint)length);
    }

    public static IntPtr Bytes(IntPtr data) => MsgSend(data, SelMutableBytes);

    /// <summary>wMaxPacketSize of the pipe's endpoint descriptor, the first member of its IOUSBHostIOSourceDescriptors.</summary>
    public static int MaxPacketSize(IntPtr pipe)
    {
        var descriptors = MsgSend(pipe, SelDescriptors);
        int size = descriptors == IntPtr.Zero ? 0 : (((byte*)descriptors)[4] | (((byte*)descriptors)[5] << 8)) & 0x7FF;
        return size > 0 ? size : FallbackPacketSize;
    }

    public static void Release(IntPtr obj)
    {
        if (obj != IntPtr.Zero)
        {
            MsgSend(obj, SelRelease);
        }
    }

    /// <summary>Closes an IOUSBHostObject's service, then drops our reference.</summary>
    public static void DestroyAndRelease(IntPtr obj)
    {
        if (obj != IntPtr.Zero)
        {
            MsgSend(obj, SelDestroy);
            MsgSend(obj, SelRelease);
        }
    }

    public static string Describe(IntPtr error)
    {
        if (error == IntPtr.Zero)
        {
            return "unknown error";
        }
        var text = MsgSend(MsgSend(error, SelDescription), SelUtf8String);
        var message = text == IntPtr.Zero ? "" : Marshal.PtrToStringUTF8(text);
        return $"0x{(int)MsgSendLong(error, SelCode):X8} {message}";
    }

    private static void SetNumber(IntPtr dict, string key, int value)
    {
        var cfKey = CFStringCreateWithCString(IntPtr.Zero, key, KCfStringEncodingUtf8);
        var cfValue = CFNumberCreate(IntPtr.Zero, KCfNumberSInt32Type, &value);
        CFDictionarySetValue(dict, cfKey, cfValue);
        CFRelease(cfValue);
        CFRelease(cfKey);
    }

    [DllImport(Libobjc, EntryPoint = "sel_registerName")]
    private static extern IntPtr Sel([MarshalAs(UnmanagedType.LPStr)] string name);

    [DllImport(Libobjc, EntryPoint = "objc_getClass")]
    private static extern IntPtr ClassGet([MarshalAs(UnmanagedType.LPStr)] string name);

    [DllImport(Libobjc, EntryPoint = "objc_msgSend")]
    private static extern IntPtr MsgSend(IntPtr receiver, IntPtr sel);

    [DllImport(Libobjc, EntryPoint = "objc_msgSend")]
    private static extern nint MsgSendLong(IntPtr receiver, IntPtr sel);

    // initWithIOService:options:queue:error:interestHandler: - (io_service_t, NSUInteger, queue, NSError**, block)
    [DllImport(Libobjc, EntryPoint = "objc_msgSend")]
    private static extern IntPtr MsgSendInit(IntPtr receiver, IntPtr sel, uint service, nuint options, IntPtr queue, IntPtr* error, IntPtr handler);

    // configurationDescriptorWithIndex:error: / copyPipeWithAddress:error: - (NSUInteger, NSError**)
    [DllImport(Libobjc, EntryPoint = "objc_msgSend")]
    private static extern IntPtr MsgSendIndexError(IntPtr receiver, IntPtr sel, nuint index, IntPtr* error);

    // configureWithValue:matchInterfaces:error: - (NSUInteger, BOOL, NSError**) -> BOOL
    [DllImport(Libobjc, EntryPoint = "objc_msgSend")]
    private static extern byte MsgSendConfigure(IntPtr receiver, IntPtr sel, nuint value, byte matchInterfaces, IntPtr* error);

    // clearStallWithError: - (NSError**) -> BOOL
    [DllImport(Libobjc, EntryPoint = "objc_msgSend")]
    private static extern byte MsgSendError(IntPtr receiver, IntPtr sel, IntPtr* error);

    // sendIORequestWithData:bytesTransferred:completionTimeout:error: - (NSMutableData*, NSUInteger*, NSTimeInterval, NSError**) -> BOOL
    [DllImport(Libobjc, EntryPoint = "objc_msgSend")]
    private static extern byte MsgSendTransfer(IntPtr receiver, IntPtr sel, IntPtr data, nuint* transferred, double timeout, IntPtr* error);

    // initWithLength: - (NSUInteger)
    [DllImport(Libobjc, EntryPoint = "objc_msgSend")]
    private static extern IntPtr MsgSendIndex(IntPtr receiver, IntPtr sel, nuint value);

    // initWithBytes:length: - (const void*, NSUInteger)
    [DllImport(Libobjc, EntryPoint = "objc_msgSend")]
    private static extern IntPtr MsgSendBytes(IntPtr receiver, IntPtr sel, byte* bytes, nuint length);

    [DllImport(Libobjc)]
    public static extern IntPtr objc_autoreleasePoolPush();

    [DllImport(Libobjc)]
    public static extern void objc_autoreleasePoolPop(IntPtr pool);

    [DllImport(IOKit)]
    private static extern IntPtr IOServiceMatching([MarshalAs(UnmanagedType.LPStr)] string name);

    [DllImport(IOKit)]
    private static extern int IOServiceGetMatchingServices(uint mainPort, IntPtr matching, out uint iterator);

    [DllImport(IOKit)]
    private static extern int IORegistryEntryGetChildIterator(uint entry, [MarshalAs(UnmanagedType.LPStr)] string plane, out uint iterator);

    [DllImport(IOKit)]
    private static extern uint IOIteratorNext(uint iterator);

    [StructLayout(LayoutKind.Sequential)]
    private struct MachTimespec
    {
        public uint Seconds;
        public int Nanoseconds;
    }

    [DllImport(IOKit)]
    private static extern int IOServiceWaitQuiet(uint service, MachTimespec* waitTime);

    public static void WaitQuiet(uint service, uint timeoutSeconds)
    {
        var timeout = new MachTimespec { Seconds = timeoutSeconds };
        IOServiceWaitQuiet(service, &timeout);
    }

    [DllImport(IOKit)]
    public static extern int IORegistryEntryInPlane(uint entry, [MarshalAs(UnmanagedType.LPStr)] string plane);

    [DllImport(IOKit)]
    private static extern int IOObjectConformsTo(uint obj, [MarshalAs(UnmanagedType.LPStr)] string className);

    [DllImport(IOKit)]
    public static extern int IOObjectRelease(uint obj);

    [DllImport(CoreFoundation)]
    private static extern IntPtr CFStringCreateWithCString(IntPtr allocator, [MarshalAs(UnmanagedType.LPStr)] string str, uint encoding);

    [DllImport(CoreFoundation)]
    private static extern IntPtr CFNumberCreate(IntPtr allocator, nint type, int* value);

    [DllImport(CoreFoundation)]
    private static extern void CFDictionarySetValue(IntPtr dict, IntPtr key, IntPtr value);

    [DllImport(CoreFoundation)]
    private static extern void CFRelease(IntPtr cf);

    [DllImport("/usr/lib/libSystem.dylib")]
    private static extern IntPtr dlopen([MarshalAs(UnmanagedType.LPStr)] string? path, int mode);
}
#endif
