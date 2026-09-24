using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;
using Nexus.Service.Models.Activity;

namespace Nexus.Service.Activity;

/// <summary>
/// macOS audio device enumeration + default-device switching via CoreAudio.
/// Device <see cref="AudioDevice.Id"/> is the CoreAudio device UID (stable across
/// reboots); switching translates the UID back to an AudioDeviceID. Mirrors the
/// CoreAudio P/Invoke style of <see cref="MacVolumeProvider"/>. AOT-safe.
/// </summary>
public sealed class MacAudioDeviceProvider : IAudioDeviceProvider
{
    public AudioDeviceList ListDevices()
    {
        var list = new AudioDeviceList();
        if (!OperatingSystem.IsMacOS()) return list;
        try
        {
            var defaultOut = GetDefaultDevice(DefaultOutputDevice);
            var defaultIn = GetDefaultDevice(DefaultInputDevice);
            foreach (var dev in GetAllDeviceIds())
            {
                var uid = GetStringProperty(dev, DeviceUID, ScopeGlobal);
                if (string.IsNullOrEmpty(uid)) continue;
                var name = GetStringProperty(dev, DeviceName, ScopeGlobal);
                if (string.IsNullOrEmpty(name)) name = uid;

                if (HasStreams(dev, ScopeOutput))
                    list.Outputs.Add(new AudioDevice { Id = uid, Name = name, IsDefault = dev == defaultOut, Direction = "output" });
                if (HasStreams(dev, ScopeInput))
                    list.Inputs.Add(new AudioDevice { Id = uid, Name = name, IsDefault = dev == defaultIn, Direction = "input" });
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[audio-mac] list failed: {ex.Message}");
        }
        return list;
    }

    public bool SetDefaultOutput(string deviceId) => SetDefault(deviceId, DefaultOutputDevice);
    public bool SetDefaultInput(string deviceId) => SetDefault(deviceId, DefaultInputDevice);
    public bool SetSpatial(string deviceId, string formatId) => false;

    private static bool SetDefault(string uid, uint selector)
    {
        if (!OperatingSystem.IsMacOS() || string.IsNullOrEmpty(uid)) return false;
        try
        {
            var dev = TranslateUidToDevice(uid);
            if (dev == 0) return false;
            var address = new AudioObjectPropertyAddress(selector, ScopeGlobal, ElementMain);
            var value = dev;
            return AudioObjectSetPropertyDataUInt32(AudioObjectSystemObject, ref address, 0, IntPtr.Zero, sizeof(uint), ref value) == 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[audio-mac] set default failed: {ex.Message}");
            return false;
        }
    }

    private static uint GetDefaultDevice(uint selector)
    {
        var address = new AudioObjectPropertyAddress(selector, ScopeGlobal, ElementMain);
        uint size = sizeof(uint);
        return AudioObjectGetPropertyDataUInt32(AudioObjectSystemObject, ref address, 0, IntPtr.Zero, ref size, out var dev) == 0 ? dev : 0;
    }

    private static List<uint> GetAllDeviceIds()
    {
        var result = new List<uint>();
        var address = new AudioObjectPropertyAddress(Devices, ScopeGlobal, ElementMain);
        if (AudioObjectGetPropertyDataSize(AudioObjectSystemObject, ref address, 0, IntPtr.Zero, out var size) != 0 || size == 0)
            return result;
        var count = (int)(size / sizeof(uint));
        var ids = new uint[count];
        if (AudioObjectGetPropertyDataUInt32Array(AudioObjectSystemObject, ref address, 0, IntPtr.Zero, ref size, ids) != 0)
            return result;
        result.AddRange(ids);
        return result;
    }

    private static bool HasStreams(uint device, uint scope)
    {
        var address = new AudioObjectPropertyAddress(Streams, scope, ElementMain);
        return AudioObjectGetPropertyDataSize(device, ref address, 0, IntPtr.Zero, out var size) == 0 && size > 0;
    }

    private static string GetStringProperty(uint device, uint selector, uint scope)
    {
        var address = new AudioObjectPropertyAddress(selector, scope, ElementMain);
        if (!AudioObjectHasProperty(device, ref address)) return "";
        uint size = (uint)IntPtr.Size;
        if (AudioObjectGetPropertyDataCFString(device, ref address, 0, IntPtr.Zero, ref size, out var cfStr) != 0 || cfStr == IntPtr.Zero)
            return "";
        try { return CfStringToManaged(cfStr); }
        finally { CFRelease(cfStr); }
    }

    /// <summary>Shared with <see cref="MacVolumeProvider"/>'s device-targeted overloads.</summary>
    internal static uint TranslateUidToDevice(string uid)
    {
        var cf = CFStringCreateWithBytes(IntPtr.Zero, Encoding.UTF8.GetBytes(uid), Encoding.UTF8.GetByteCount(uid), kCFStringEncodingUTF8, false);
        if (cf == IntPtr.Zero) return 0;
        try
        {
            var address = new AudioObjectPropertyAddress(TranslateUidToDeviceSelector, ScopeGlobal, ElementMain);
            uint outDev = 0;
            uint outSize = sizeof(uint);
            var cfLocal = cf;
            // Qualifier = pointer to the CFStringRef UID; out = AudioDeviceID.
            var status = AudioObjectGetPropertyDataTranslate(AudioObjectSystemObject, ref address, (uint)IntPtr.Size, ref cfLocal, ref outSize, out outDev);
            return status == 0 ? outDev : 0;
        }
        finally { CFRelease(cf); }
    }

    private static string CfStringToManaged(IntPtr cfStr)
    {
        var len = CFStringGetLength(cfStr);
        if (len == 0) return "";
        var maxBytes = (int)CFStringGetMaximumSizeForEncoding(len, kCFStringEncodingUTF8) + 1;
        var buffer = new byte[maxBytes];
        if (!CFStringGetCString(cfStr, buffer, maxBytes, kCFStringEncodingUTF8)) return "";
        var n = Array.IndexOf(buffer, (byte)0);
        return Encoding.UTF8.GetString(buffer, 0, n < 0 ? buffer.Length : n);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct AudioObjectPropertyAddress
    {
        public uint Selector;
        public uint Scope;
        public uint Element;
        public AudioObjectPropertyAddress(uint selector, uint scope, uint element)
        {
            Selector = selector; Scope = scope; Element = element;
        }
    }

    private static uint FourCc(char a, char b, char c, char d) =>
        ((uint)a << 24) | ((uint)b << 16) | ((uint)c << 8) | d;

    private const string CoreAudio = "/System/Library/Frameworks/CoreAudio.framework/CoreAudio";
    private const string CoreFoundation = "/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation";
    private const uint AudioObjectSystemObject = 1;
    private const uint ElementMain = 0;
    private const uint kCFStringEncodingUTF8 = 0x08000100;
    private static readonly uint ScopeGlobal = FourCc('g', 'l', 'o', 'b');
    private static readonly uint ScopeOutput = FourCc('o', 'u', 't', 'p');
    private static readonly uint ScopeInput = FourCc('i', 'n', 'p', 't');
    private static readonly uint Devices = FourCc('d', 'e', 'v', '#');
    private static readonly uint DefaultOutputDevice = FourCc('d', 'O', 'u', 't');
    private static readonly uint DefaultInputDevice = FourCc('d', 'I', 'n', ' ');
    private static readonly uint DeviceName = FourCc('l', 'n', 'a', 'm');
    private static readonly uint DeviceUID = FourCc('u', 'i', 'd', ' ');
    private static readonly uint Streams = FourCc('s', 't', 'm', '#');
    private static readonly uint TranslateUidToDeviceSelector = FourCc('u', 'i', 'd', 'd');

    [DllImport(CoreAudio, EntryPoint = "AudioObjectHasProperty")]
    [return: MarshalAs(UnmanagedType.I1)]
    private static extern bool AudioObjectHasProperty(uint id, ref AudioObjectPropertyAddress addr);

    [DllImport(CoreAudio, EntryPoint = "AudioObjectGetPropertyDataSize")]
    private static extern int AudioObjectGetPropertyDataSize(uint id, ref AudioObjectPropertyAddress addr, uint qDataSize, IntPtr qData, out uint outSize);

    [DllImport(CoreAudio, EntryPoint = "AudioObjectGetPropertyData")]
    private static extern int AudioObjectGetPropertyDataUInt32(uint id, ref AudioObjectPropertyAddress addr, uint qDataSize, IntPtr qData, ref uint ioSize, out uint outData);

    [DllImport(CoreAudio, EntryPoint = "AudioObjectGetPropertyData")]
    private static extern int AudioObjectGetPropertyDataUInt32Array(uint id, ref AudioObjectPropertyAddress addr, uint qDataSize, IntPtr qData, ref uint ioSize, [Out] uint[] outData);

    [DllImport(CoreAudio, EntryPoint = "AudioObjectGetPropertyData")]
    private static extern int AudioObjectGetPropertyDataCFString(uint id, ref AudioObjectPropertyAddress addr, uint qDataSize, IntPtr qData, ref uint ioSize, out IntPtr outData);

    [DllImport(CoreAudio, EntryPoint = "AudioObjectGetPropertyData")]
    private static extern int AudioObjectGetPropertyDataTranslate(uint id, ref AudioObjectPropertyAddress addr, uint qDataSize, ref IntPtr qData, ref uint ioSize, out uint outData);

    [DllImport(CoreAudio, EntryPoint = "AudioObjectSetPropertyData")]
    private static extern int AudioObjectSetPropertyDataUInt32(uint id, ref AudioObjectPropertyAddress addr, uint qDataSize, IntPtr qData, uint dataSize, ref uint inData);

    [DllImport(CoreFoundation)]
    private static extern IntPtr CFStringCreateWithBytes(IntPtr alloc, byte[] bytes, long numBytes, uint encoding, [MarshalAs(UnmanagedType.I1)] bool isExternalRepresentation);

    [DllImport(CoreFoundation)]
    private static extern long CFStringGetLength(IntPtr theString);

    [DllImport(CoreFoundation)]
    private static extern long CFStringGetMaximumSizeForEncoding(long length, uint encoding);

    [DllImport(CoreFoundation)]
    [return: MarshalAs(UnmanagedType.I1)]
    private static extern bool CFStringGetCString(IntPtr theString, byte[] buffer, long bufferSize, uint encoding);

    [DllImport(CoreFoundation)]
    private static extern void CFRelease(IntPtr cf);
}
