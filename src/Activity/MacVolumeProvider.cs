using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Nexus.Service.Models.Activity;

namespace Nexus.Service.Activity;

/// <summary>
/// macOS default-output volume via CoreAudio. Some output devices expose a
/// main-element volume, while others only expose per-channel controls; both
/// paths are probed so fixed-volume outputs report unsupported.
/// </summary>
public sealed class MacVolumeProvider : IVolumeProvider
{
    private readonly object _gate = new();

    public VolumeState GetState() => GetState("");

    public VolumeState GetState(string deviceId)
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
            return UnsupportedState();

        lock (_gate)
        {
            try
            {
                if (!TryResolveDevice(deviceId, out var device))
                    return UnsupportedState();

                var targets = GetVolumeTargets(device);
                if (targets.Count == 0)
                    return UnsupportedState();

                var volume = ReadAverage(device, VolumeScalar, targets);
                if (!volume.HasValue)
                    return UnsupportedState();

                return new VolumeState
                {
                    Supported = true,
                    Volume = Math.Clamp(volume.Value, 0, 1),
                    Muted = ReadMuted(device, targets),
                };
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[volume-mac] read failed: {ex.Message}");
                return UnsupportedState();
            }
        }
    }

    public void SetVolume(double volume) => SetVolume("", volume);

    public void SetVolume(string deviceId, double volume)
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
            return;

        var clamped = (float)Math.Clamp(volume, 0, 1);
        lock (_gate)
        {
            try
            {
                if (!TryResolveDevice(deviceId, out var device))
                    return;

                foreach (var target in GetVolumeTargets(device))
                {
                    TrySetFloat(device, VolumeScalar, target, clamped);
                }
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[volume-mac] set failed: {ex.Message}");
            }
        }
    }

    public void SetMuted(bool muted) => SetMuted("", muted);

    public void SetMuted(string deviceId, bool muted)
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
            return;

        lock (_gate)
        {
            try
            {
                if (!TryResolveDevice(deviceId, out var device))
                    return;

                var value = muted ? 1u : 0u;
                foreach (var target in GetMuteTargets(device))
                {
                    TrySetUInt32(device, Mute, target, value);
                }
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[volume-mac] mute failed: {ex.Message}");
            }
        }
    }

    /// <summary>Empty <paramref name="deviceId"/> resolves the default output;
    /// otherwise translates the CoreAudio UID to its AudioDeviceID.</summary>
    private static bool TryResolveDevice(string deviceId, out uint device)
    {
        if (deviceId.Length == 0)
            return TryGetDefaultOutputDevice(out device);

        device = MacAudioDeviceProvider.TranslateUidToDevice(deviceId);
        return device != 0;
    }

    private static VolumeState UnsupportedState() => new() { Supported = false, Volume = 0, Muted = false };

    private static List<PropertyTarget> GetVolumeTargets(uint device)
    {
        var main = new PropertyTarget(ScopeOutput, ElementMain);
        if (IsSettable(device, VolumeScalar, main) && HasReadableFloat(device, VolumeScalar, main))
            return new List<PropertyTarget> { main };

        var targets = new List<PropertyTarget>();
        foreach (var channel in GetPreferredStereoChannels(device))
        {
            var target = new PropertyTarget(ScopeOutput, channel);
            if (IsSettable(device, VolumeScalar, target) && HasReadableFloat(device, VolumeScalar, target))
                targets.Add(target);
        }
        return targets;
    }

    private static List<PropertyTarget> GetMuteTargets(uint device)
    {
        var main = new PropertyTarget(ScopeOutput, ElementMain);
        if (IsSettable(device, Mute, main))
            return new List<PropertyTarget> { main };

        var targets = new List<PropertyTarget>();
        foreach (var channel in GetPreferredStereoChannels(device))
        {
            var target = new PropertyTarget(ScopeOutput, channel);
            if (IsSettable(device, Mute, target))
                targets.Add(target);
        }
        return targets;
    }

    private static double? ReadAverage(uint device, uint selector, IReadOnlyList<PropertyTarget> targets)
    {
        if (targets.Count == 0)
            return null;

        double sum = 0;
        var count = 0;
        foreach (var target in targets)
        {
            if (!TryGetFloat(device, selector, target, out var value))
                continue;

            sum += value;
            count++;
        }

        return count == 0 ? null : sum / count;
    }

    private static bool ReadMuted(uint device, IReadOnlyList<PropertyTarget> volumeTargets)
    {
        var main = new PropertyTarget(ScopeOutput, ElementMain);
        if (TryGetUInt32(device, Mute, main, out var mainMuted))
            return mainMuted != 0;

        foreach (var target in volumeTargets)
        {
            if (!TryGetUInt32(device, Mute, target, out var muted))
                continue;

            if (muted != 0)
                return true;
        }

        return false;
    }

    private static bool TryGetDefaultOutputDevice(out uint device)
    {
        device = 0;
        var address = new AudioObjectPropertyAddress(DefaultOutputDevice, ScopeGlobal, ElementMain);
        uint size = sizeof(uint);
        var status = AudioObjectGetPropertyDataUInt32(
            AudioObjectSystemObject,
            ref address,
            0,
            IntPtr.Zero,
            ref size,
            out device);
        return status == 0 && device != 0;
    }

    private static IReadOnlyList<uint> GetPreferredStereoChannels(uint device)
    {
        var channels = new uint[2];
        if (TryGetPreferredStereoChannels(device, ScopeOutput, channels) ||
            TryGetPreferredStereoChannels(device, ScopeGlobal, channels))
        {
            var result = new List<uint>(2);
            foreach (var channel in channels)
            {
                if (channel != 0)
                    result.Add(channel);
            }
            return result;
        }

        return Array.Empty<uint>();
    }

    private static bool TryGetPreferredStereoChannels(uint device, uint scope, uint[] channels)
    {
        Array.Clear(channels);
        var address = new AudioObjectPropertyAddress(PreferredStereoChannelsForStereo, scope, ElementMain);
        if (!AudioObjectHasProperty(device, ref address))
            return false;

        var size = (uint)(sizeof(uint) * channels.Length);
        var status = AudioObjectGetPropertyDataUInt32Array(
            device,
            ref address,
            0,
            IntPtr.Zero,
            ref size,
            channels);
        return status == 0 && channels[0] != 0;
    }

    private static bool IsSettable(uint device, uint selector, PropertyTarget target)
    {
        var address = target.ToAddress(selector);
        return AudioObjectHasProperty(device, ref address) &&
               AudioObjectIsPropertySettable(device, ref address, out var settable) == 0 &&
               settable;
    }

    private static bool HasReadableFloat(uint device, uint selector, PropertyTarget target) =>
        TryGetFloat(device, selector, target, out _);

    private static bool TryGetFloat(uint device, uint selector, PropertyTarget target, out float value)
    {
        value = 0;
        var address = target.ToAddress(selector);
        if (!AudioObjectHasProperty(device, ref address))
            return false;

        uint size = sizeof(float);
        return AudioObjectGetPropertyDataFloat(
            device,
            ref address,
            0,
            IntPtr.Zero,
            ref size,
            out value) == 0;
    }

    private static bool TrySetFloat(uint device, uint selector, PropertyTarget target, float value)
    {
        var address = target.ToAddress(selector);
        uint size = sizeof(float);
        return AudioObjectHasProperty(device, ref address) &&
               AudioObjectIsPropertySettable(device, ref address, out var settable) == 0 &&
               settable &&
               AudioObjectSetPropertyDataFloat(
                   device,
                   ref address,
                   0,
                   IntPtr.Zero,
                   size,
                   ref value) == 0;
    }

    private static bool TryGetUInt32(uint device, uint selector, PropertyTarget target, out uint value)
    {
        value = 0;
        var address = target.ToAddress(selector);
        if (!AudioObjectHasProperty(device, ref address))
            return false;

        uint size = sizeof(uint);
        return AudioObjectGetPropertyDataUInt32(
            device,
            ref address,
            0,
            IntPtr.Zero,
            ref size,
            out value) == 0;
    }

    private static bool TrySetUInt32(uint device, uint selector, PropertyTarget target, uint value)
    {
        var address = target.ToAddress(selector);
        uint size = sizeof(uint);
        return AudioObjectHasProperty(device, ref address) &&
               AudioObjectIsPropertySettable(device, ref address, out var settable) == 0 &&
               settable &&
               AudioObjectSetPropertyDataUInt32(
                   device,
                   ref address,
                   0,
                   IntPtr.Zero,
                   size,
                   ref value) == 0;
    }

    private readonly record struct PropertyTarget(uint Scope, uint Element)
    {
        public AudioObjectPropertyAddress ToAddress(uint selector) => new(selector, Scope, Element);
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct AudioObjectPropertyAddress
    {
        public uint Selector;
        public uint Scope;
        public uint Element;

        public AudioObjectPropertyAddress(uint selector, uint scope, uint element)
        {
            Selector = selector;
            Scope = scope;
            Element = element;
        }
    }

    private static uint FourCc(char a, char b, char c, char d) =>
        ((uint)a << 24) | ((uint)b << 16) | ((uint)c << 8) | d;

    private const string CoreAudio = "/System/Library/Frameworks/CoreAudio.framework/CoreAudio";
    private const uint AudioObjectSystemObject = 1;
    private const uint ElementMain = 0;
    private static readonly uint ScopeGlobal = FourCc('g', 'l', 'o', 'b');
    private static readonly uint ScopeOutput = FourCc('o', 'u', 't', 'p');
    private static readonly uint DefaultOutputDevice = FourCc('d', 'O', 'u', 't');
    private static readonly uint VolumeScalar = FourCc('v', 'o', 'l', 'm');
    private static readonly uint Mute = FourCc('m', 'u', 't', 'e');
    private static readonly uint PreferredStereoChannelsForStereo = FourCc('d', 'c', 'h', '2');

    [DllImport(CoreAudio, EntryPoint = "AudioObjectHasProperty")]
    [return: MarshalAs(UnmanagedType.I1)]
    private static extern bool AudioObjectHasProperty(
        uint inObjectID,
        ref AudioObjectPropertyAddress inAddress);

    [DllImport(CoreAudio)]
    private static extern int AudioObjectIsPropertySettable(
        uint inObjectID,
        ref AudioObjectPropertyAddress inAddress,
        [MarshalAs(UnmanagedType.I1)] out bool outIsSettable);

    [DllImport(CoreAudio, EntryPoint = "AudioObjectGetPropertyData")]
    private static extern int AudioObjectGetPropertyDataUInt32(
        uint inObjectID,
        ref AudioObjectPropertyAddress inAddress,
        uint inQualifierDataSize,
        IntPtr inQualifierData,
        ref uint ioDataSize,
        out uint outData);

    [DllImport(CoreAudio, EntryPoint = "AudioObjectGetPropertyData")]
    private static extern int AudioObjectGetPropertyDataFloat(
        uint inObjectID,
        ref AudioObjectPropertyAddress inAddress,
        uint inQualifierDataSize,
        IntPtr inQualifierData,
        ref uint ioDataSize,
        out float outData);

    [DllImport(CoreAudio, EntryPoint = "AudioObjectGetPropertyData")]
    private static extern int AudioObjectGetPropertyDataUInt32Array(
        uint inObjectID,
        ref AudioObjectPropertyAddress inAddress,
        uint inQualifierDataSize,
        IntPtr inQualifierData,
        ref uint ioDataSize,
        [Out] uint[] outData);

    [DllImport(CoreAudio, EntryPoint = "AudioObjectSetPropertyData")]
    private static extern int AudioObjectSetPropertyDataUInt32(
        uint inObjectID,
        ref AudioObjectPropertyAddress inAddress,
        uint inQualifierDataSize,
        IntPtr inQualifierData,
        uint inDataSize,
        ref uint inData);

    [DllImport(CoreAudio, EntryPoint = "AudioObjectSetPropertyData")]
    private static extern int AudioObjectSetPropertyDataFloat(
        uint inObjectID,
        ref AudioObjectPropertyAddress inAddress,
        uint inQualifierDataSize,
        IntPtr inQualifierData,
        uint inDataSize,
        ref float inData);
}
