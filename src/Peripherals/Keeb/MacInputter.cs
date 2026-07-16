using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading;
using Nexus.Service.Models.Peripherals.Keeb;
using Nexus.Service.Platform;

namespace Nexus.Service.Peripherals.Keeb;

/// <summary>
/// macOS keyboard input injection via CoreGraphics CGEvent - the macOS
/// counterpart to <see cref="WindowsInputter"/>'s SendInput and
/// <see cref="LinuxInputter"/>'s uinput paths. Modifiers are applied as
/// CGEventFlags on the key event itself (the idiomatic CGEvent chord) rather
/// than as separate key presses. AOT-safe: plain <c>[DllImport]</c> of scalar /
/// IntPtr signatures (same model as MacVolumeProvider / MacDisplayBrightnessProvider).
///
/// Requires Accessibility permission (System Settings → Privacy &amp; Security →
/// Accessibility). Without it CGEventPost silently no-ops; callers can probe
/// <see cref="AccessibilityGranted"/> first.
/// </summary>
public sealed class MacInputter : IInputterProvider
{
    private bool _warned;

    public void Send(InputterBody body)
    {
        if (!OperatingSystem.IsMacOS()) return;
        if (body.Strokes is null || body.Strokes.Count == 0) return;

        if (!AccessibilityGranted() && !_warned)
        {
            _warned = true;
            ServiceLog.Warn("[inputter-mac] Accessibility permission not granted - keyboard injection will no-op. Grant it in System Settings → Privacy & Security → Accessibility.");
        }

        foreach (var stroke in body.Strokes)
        {
            var vk = ParseKey(stroke.Key);
            if (vk == 0xFFFF) continue;

            ulong flags = 0;
            if (stroke.Ctrl) flags |= kCGEventFlagMaskControl;
            if (stroke.Shift) flags |= kCGEventFlagMaskShift;
            if (stroke.Alt) flags |= kCGEventFlagMaskAlternate;
            if (stroke.Meta) flags |= kCGEventFlagMaskCommand;

            var down = string.Equals(stroke.Type, "keydown", StringComparison.OrdinalIgnoreCase);

            var ev = CGEventCreateKeyboardEvent(IntPtr.Zero, vk, down);
            if (ev == IntPtr.Zero) continue;
            try
            {
                if (flags != 0) CGEventSetFlags(ev, flags);
                CGEventPost(kCGHIDEventTap, ev);
            }
            finally
            {
                CFRelease(ev);
            }

            if (stroke.Duration > 0)
                Thread.Sleep(stroke.Duration);
        }
    }

    /// <summary>True when the process is trusted for Accessibility (event injection works).</summary>
    public static bool AccessibilityGranted()
    {
        if (!OperatingSystem.IsMacOS()) return false;
        try { return AXIsProcessTrusted(); }
        catch { return false; }
    }

    /// <summary>
    /// Maps the canonical MacroStroke key name to a macOS ANSI virtual keycode.
    /// Returns 0xFFFF for names with no CGKeyboardEvent mapping on macOS
    /// (Insert / NumLock / ScrollLock / PrintScreen / Pause / ContextMenu /
    /// F21-F24 and the NX media keys) so the caller skips them. The accepted
    /// name set matches WindowsInputter.ParseKey / LinuxInputter.ParseKey.
    /// </summary>
    internal static ushort ParseKey(string key)
    {
        if (string.IsNullOrEmpty(key)) return 0xFFFF;

        // Letters: KeyA..KeyZ
        if (key.Length == 4 && key.StartsWith("Key", StringComparison.Ordinal))
        {
            var c = char.ToUpperInvariant(key[3]);
            if (c is >= 'A' and <= 'Z') return Letters[c - 'A'];
            return 0xFFFF;
        }

        // Digits: Digit0..Digit9
        if (key.Length == 6 && key.StartsWith("Digit", StringComparison.Ordinal))
        {
            var d = key[5];
            if (d is >= '0' and <= '9') return Digits[d - '0'];
            return 0xFFFF;
        }

        // Function keys: F1..F24 (F21-F24 unmapped on macOS → 0xFFFF)
        if (key.Length is 2 or 3 && key[0] == 'F'
            && int.TryParse(key.AsSpan(1), out var fn) && fn is >= 1 and <= 24)
        {
            return FKeys.TryGetValue(fn, out var fc) ? fc : (ushort)0xFFFF;
        }

        return Named.TryGetValue(key, out var code) ? code : (ushort)0xFFFF;
    }

    // kVK_ANSI_* keycodes (Carbon HIToolbox/Events.h), A..Z
    private static readonly ushort[] Letters =
    {
        0, 11, 8, 2, 14, 3, 5, 4, 34, 38, 40, 37, 46, // A-M
        45, 31, 35, 12, 15, 1, 17, 32, 9, 13, 7, 16, 6, // N-Z
    };

    // kVK_ANSI_0..9
    private static readonly ushort[] Digits = { 29, 18, 19, 20, 21, 23, 22, 26, 28, 25 };

    private static readonly Dictionary<int, ushort> FKeys = new()
    {
        [1] = 122, [2] = 120, [3] = 99, [4] = 118, [5] = 96, [6] = 97, [7] = 98,
        [8] = 100, [9] = 101, [10] = 109, [11] = 103, [12] = 111, [13] = 105,
        [14] = 107, [15] = 113, [16] = 106, [17] = 64, [18] = 79, [19] = 80, [20] = 90,
    };

    private static readonly Dictionary<string, ushort> Named = new()
    {
        ["Space"] = 49,
        ["Enter"] = 36,
        ["Tab"] = 48,
        ["Escape"] = 53,
        ["Backspace"] = 51,
        ["Delete"] = 117,        // forward delete
        ["Home"] = 115,
        ["End"] = 119,
        ["PageUp"] = 116,
        ["PageDown"] = 121,
        ["ArrowUp"] = 126,
        ["ArrowDown"] = 125,
        ["ArrowLeft"] = 123,
        ["ArrowRight"] = 124,
        ["CapsLock"] = 57,
        ["Period"] = 47, // kVK_ANSI_Period
        // No macOS CGKeyboardEvent mapping (skipped): Insert, NumLock,
        // ScrollLock, PrintScreen, Pause, ContextMenu, and the media keys.
    };

    // ── CoreGraphics / CoreFoundation / ApplicationServices P/Invoke ──
    private const string CoreGraphics = "/System/Library/Frameworks/CoreGraphics.framework/CoreGraphics";
    private const string CoreFoundation = "/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation";
    private const string ApplicationServices = "/System/Library/Frameworks/ApplicationServices.framework/ApplicationServices";

    private const uint kCGHIDEventTap = 0;
    private const ulong kCGEventFlagMaskShift = 0x20000;
    private const ulong kCGEventFlagMaskControl = 0x40000;
    private const ulong kCGEventFlagMaskAlternate = 0x80000;
    private const ulong kCGEventFlagMaskCommand = 0x100000;

    [DllImport(CoreGraphics)]
    private static extern IntPtr CGEventCreateKeyboardEvent(IntPtr source, ushort virtualKey, [MarshalAs(UnmanagedType.I1)] bool keyDown);

    [DllImport(CoreGraphics)]
    private static extern void CGEventSetFlags(IntPtr handle, ulong flags);

    [DllImport(CoreGraphics)]
    private static extern void CGEventPost(uint tap, IntPtr handle);

    [DllImport(CoreFoundation)]
    private static extern void CFRelease(IntPtr cf);

    [DllImport(ApplicationServices)]
    [return: MarshalAs(UnmanagedType.I1)]
    private static extern bool AXIsProcessTrusted();
}
