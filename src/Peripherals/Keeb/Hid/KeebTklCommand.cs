using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Qos.Service.Peripherals.Hid;
using Qos.Service.Peripherals.Keeb.Hid.Enums;
using Qos.Service.Peripherals.Keeb.Hid.Layout;

namespace Qos.Service.Peripherals.Keeb.Hid;

/// <summary>
/// Low-level HID protocol wrapper for the HYTE Keeb TKL (Suoai-built, VID 0x3402 /
/// PID 0x0300). Ported from nexus-control-service/LightDancing/Common/KeebTKLCommand.cs.
///
/// The keeb exposes two HID interfaces: one for control + 65-byte feature
/// responses (the "feature" interface), and one for 8-byte interrupt IN
/// reports carrying key/scroll/profile events (the "input" interface). All
/// of the commands here serialize through a single lock — the firmware
/// crashes if requests interleave.
///
/// Wire-level summary (preserved verbatim from the legacy port):
///   00 84 05 ...   GetDeviceInfo (FW version + ANSI/ISO layout in response byte 7)
///   00 84 06 ...   GetSettings   (65 bytes: game mode + FW animation + scroll mode)
///   00 04 06 ...   SetSettings   (writes one 65-byte page)
///   00 84 F2 P L   GetLayerKeyAssignment (8 x 65 = 512 useful bytes)
///   00 04 F2 P L   SetLayerKeyAssignment (8 x 65 pages)
///   00 04 02 P     SelectCurrentProfile
///   00 84 02 ...   GetCurrentProfileIndex (response over the input pipe)
///   00 04 F3 0 I   SetMacro (4 x 65 pages, ≤ 256 useful bytes)
///   00 84 F3 0 I   GetMacro
///   00 04 (LedStreamingParts) Streaming (6 middle + 3 surround pages of RGB)
///
/// Input-stream parsing (8-byte reads, all start with 0x05):
///   [1]=0x09 Scrollwheel — [2]/[3] encode left/right + top/bottom + button
///   [1]=0xFA KeyPress   — [2]/[3] = firmware matrix (x, y)
///   [1]=0xF1 SoftwareMode — [2] = software flag
///   [1]=0x02 Profile     — [2] = current profile index (response to GetCurrentProfileIndex)
/// </summary>
public sealed class KeebTklCommand : IDisposable
{
    private const int SLEEP_MS = 20;
    private const int MIDDLE_RGB_PACKAGE_COUNT = 6;
    private const int SURROUND_RGB_PACKAGE_COUNT = 3;

    private readonly IHidDevice _feature;
    private readonly IHidDevice _input;
    private readonly object _lock = new();
    private CancellationTokenSource? _cancelSource = new();
    private Task? _readingTask;
    private bool _isWaitingToGetProfileIndex;
    private int _profileIndex;
    private int _scrollInputThrottleMs = 100;
    private long _lastScrollTickMs;
    private ScrollWheelsMode _lastScrollwheelMode = ScrollWheelsMode.NA;
    private KeebLayout _layout = KeebLayout.ANSI;

    public Action<ScrollWheelsMode>? ScrollWheelInvoke { get; set; }
    public Action<byte>? SoftwareKeyInvoke { get; set; }
    public Action<KeebLayoutModel>? KeyPressInvoke { get; set; }

    public KeebTklCommand(IHidDevice featureInterface, IHidDevice inputInterface)
    {
        _feature = featureInterface;
        _input = inputInterface;
        _layout = ReadDeviceLayout();
        StartReadingStreamInput();
    }

    #region Device + settings reads

    /// <summary>Returns the 65-byte response to a device-info query (FW version in bytes 5..6, layout in byte 7).</summary>
    public byte[] GetDeviceInfo()
    {
        lock (_lock)
        {
            var query = new byte[] { 0x00, 0x84, 0x05, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00 };
            SetFeatureSafe(query, "GetDeviceInfo");
            Sleep(SLEEP_MS);
            var resp = new byte[65];
            ReadSafe(resp, "GetDeviceInfo read");
            Sleep(SLEEP_MS);
            return resp;
        }
    }

    /// <summary>Returns the 65-byte keeb-settings page (game mode + FW animation + scroll mode + 8 LED colors).</summary>
    public byte[] GetSettings()
    {
        lock (_lock)
        {
            var query = new byte[] { 0x00, 0x84, 0x06, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00 };
            SetFeatureSafe(query, "GetSettings");
            Sleep(SLEEP_MS);
            var resp = new byte[65];
            ReadSafe(resp, "GetSettings read");
            Sleep(SLEEP_MS);
            return resp;
        }
    }

    /// <summary>Writes a single 65-byte settings page back to the firmware.</summary>
    public void SetSettings(byte[] page)
    {
        lock (_lock)
        {
            var query = new byte[] { 0x00, 0x04, 0x06, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00 };
            SetFeatureSafe(query, "SetSettings query");
            Sleep(SLEEP_MS);
            WriteSafe(page, "SetSettings write");
            Sleep(SLEEP_MS);
        }
    }

    #endregion

    #region Layer key assignment

    /// <summary>Returns 512 useful bytes (8 × 64) for one profile/layer's key map.</summary>
    public byte[] GetLayerKeyAssignment(int profile, int layer)
    {
        lock (_lock)
        {
            var query = new byte[] { 0x00, 0x84, 0xF2, (byte)profile, (byte)layer, 0x00, 0x00, 0x00, 0x00 };
            SetFeatureSafe(query, "GetLayerKeyAssignment query");
            Sleep(SLEEP_MS);
            var result = new byte[512];
            var page = new byte[65];
            for (int i = 0; i < 8; i++)
            {
                ReadSafe(page, $"GetLayerKeyAssignment page {i}");
                Array.Copy(page, 1, result, i * 64, 64);
                Sleep(5);
            }
            return result;
        }
    }

    /// <summary>Writes 512 useful bytes for one profile/layer's key map (8 pages × 65 bytes).</summary>
    public void SetLayerKeyAssignment(int profile, int layer, byte[] commands)
    {
        lock (_lock)
        {
            var query = new byte[] { 0x00, 0x04, 0xF2, (byte)profile, (byte)layer, 0x00, 0x00, 0x00, 0x00 };
            SetFeatureSafe(query, "SetLayerKeyAssignment query");
            Sleep(SLEEP_MS);
            for (int i = 0; i < commands.Length / 64; i++)
            {
                var page = new byte[65];
                Array.Copy(commands, i * 64, page, 1, 64);
                WriteSafe(page, $"SetLayerKeyAssignment page {i}");
                // Legacy nexus-control-service shipped with the inter-page
                // wait commented out (the `_timerForStream5` reference). On
                // this firmware revision the writes silently drop without it
                // - bench-confirmed on T1 2026-05-20: the readback after
                // SetLayer showed the previous (default) bytes regardless of
                // what was sent. SetMacro already paces its 4 pages; we mirror
                // that here to keep all multi-page writes uniform.
                Sleep(SLEEP_MS);
            }
        }
    }

    #endregion

    #region Profile selection

    /// <summary>Pins firmware to the given profile index (0 or 1).</summary>
    public void SelectCurrentProfile(int profile)
    {
        lock (_lock)
        {
            var query = new byte[] { 0x00, 0x04, 0x02, (byte)profile, 0x00, 0x00, 0x00, 0x00, 0x00 };
            SetFeatureSafe(query, "SelectCurrentProfile");
            Sleep(SLEEP_MS);
        }
    }

    /// <summary>Polls the firmware for the active profile index. Response arrives on the input pipe.</summary>
    public int GetCurrentProfileIndex()
    {
        lock (_lock)
        {
            _isWaitingToGetProfileIndex = true;
            var query = new byte[] { 0x00, 0x84, 0x02, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00 };
            SetFeatureSafe(query, "GetCurrentProfileIndex");

            int count = 0;
            while (_isWaitingToGetProfileIndex)
            {
                count++;
                if (count > 50) SetFeatureSafe(query, "GetCurrentProfileIndex retry");
                if (count > 100) break;
                Thread.Sleep(50);
            }
            return _profileIndex;
        }
    }

    #endregion

    #region Macros

    /// <summary>Writes up to 256 bytes of macro data to the given slot (4 pages × 65 bytes). Slot index is 0..31 (profile × 16 + macro).</summary>
    public void SetMacro(int slot, byte[] macroBytes)
    {
        lock (_lock)
        {
            var query = new byte[] { 0x00, 0x04, 0xF3, 0x00, (byte)slot, 0x00, 0x00, 0x00, 0x00 };
            SetFeatureSafe(query, "SetMacro query");
            Sleep(SLEEP_MS);
            for (int i = 0; i < 4; i++)
            {
                var page = new byte[65];
                if (macroBytes.Length > 64 * (i + 1))
                {
                    Array.Copy(macroBytes, 64 * i, page, 1, 64);
                }
                else if (macroBytes.Length > 64 * i)
                {
                    Array.Copy(macroBytes, 64 * i, page, 1, macroBytes.Length - 64 * i);
                }
                WriteSafe(page, $"SetMacro page {i}");
                Sleep(SLEEP_MS);
            }
        }
    }

    /// <summary>Returns the full 256-byte macro payload (4 × 64 useful bytes) for the given slot.</summary>
    public List<byte> GetMacro(int slot)
    {
        lock (_lock)
        {
            var query = new byte[] { 0x00, 0x84, 0xF3, 0x00, (byte)slot, 0x00, 0x00, 0x00, 0x00 };
            SetFeatureSafe(query, "GetMacro query");
            Sleep(SLEEP_MS);
            var page = new byte[65];
            var result = new List<byte>(256);
            for (int i = 0; i < 4; i++)
            {
                ReadSafe(page, $"GetMacro page {i}");
                var chunk = new byte[64];
                Array.Copy(page, 1, chunk, 0, 64);
                result.AddRange(chunk);
            }
            return result;
        }
    }

    #endregion

    #region RGB streaming (used by lighting engine — not by customization UI)

    /// <summary>Pushes 9 × 64 bytes of RGB data to the keeb (6 middle pages + 3 surround pages).</summary>
    public void Streaming(List<byte> displayColors)
    {
        lock (_lock)
        {
            if (displayColors == null || displayColors.Count < MIDDLE_RGB_PACKAGE_COUNT * 64) return;

            var middleHeader = new byte[] { 0x00, 0x04, (byte)LedStreamingParts.Middle, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00 };
            SetFeatureSafe(middleHeader, "Streaming middle header");

            for (int i = 0; i < MIDDLE_RGB_PACKAGE_COUNT; i++)
            {
                var color = displayColors.GetRange(64 * i, 64);
                color.Insert(0, 0x00);
                WriteSafe(color.ToArray(), $"Streaming middle page {i}");
            }

            var surroundHeader = new byte[] { 0x00, 0x04, (byte)LedStreamingParts.Surround, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00 };
            SetFeatureSafe(surroundHeader, "Streaming surround header");

            for (int i = MIDDLE_RGB_PACKAGE_COUNT; i < MIDDLE_RGB_PACKAGE_COUNT + SURROUND_RGB_PACKAGE_COUNT; i++)
            {
                var color = displayColors.GetRange(64 * i, 64);
                color.Insert(0, 0x00);
                WriteSafe(color.ToArray(), $"Streaming surround page {i}");
            }
        }
    }

    #endregion

    #region Scroll wheel sensitivity

    /// <summary>Sets the minimum delay between scroll-wheel events forwarded to the host. Maps directly onto the sensitivity slider in the UI.</summary>
    public void ChangeScrollwheelSensitivity(int delayMs)
    {
        _scrollInputThrottleMs = delayMs;
    }

    #endregion

    #region Input stream

    private KeebLayout ReadDeviceLayout()
    {
        try
        {
            var info = GetDeviceInfo();
            return (KeebLayout)info[7];
        }
        catch
        {
            return KeebLayout.ANSI;
        }
    }

    private void StartReadingStreamInput()
    {
        // We re-resolve the layout-model list each iteration so the binding survives
        // a hot-swap between ANSI/ISO. Most users don't swap; the cost is one
        // dictionary lookup per input report which is negligible.
        _readingTask = Task.Run(async () =>
        {
            var token = _cancelSource?.Token ?? CancellationToken.None;
            var buffer = new byte[8];

            while (_cancelSource is not null && !token.IsCancellationRequested)
            {
                try
                {
                    var read = await Task.Run(() => _input.Read(buffer, 1000), token);
                    if (read <= 0) continue;
                    ProcessInputReport(buffer);
                    Array.Clear(buffer, 0, buffer.Length);
                }
                catch (OperationCanceledException) { break; }
                catch
                {
                    // Hot-swap or device close — stop reading.
                    if (_cancelSource?.IsCancellationRequested ?? true) break;
                    await Task.Delay(50, token);
                }
            }
        });
    }

    private void ProcessInputReport(byte[] report)
    {
        if (report[0] != 0x05) return;

        var cmd = (InputCommandMode)report[1];

        switch (cmd)
        {
            case InputCommandMode.Scrollwheel:
                // [3] encodes the left wheel (and the centre-press button); [2] encodes the right wheel.
                switch (report[3])
                {
                    case 0x20: TryEmitScroll(ScrollWheelsMode.ScrollWheels_TopLeft);    break;
                    case 0x40: TryEmitScroll(ScrollWheelsMode.ScrollWheels_BottomLeft); break;
                    case 0x10: TryEmitScroll(ScrollWheelsMode.ScrollWheels_Button);     break;
                }
                switch (report[2])
                {
                    case 0x20: TryEmitScroll(ScrollWheelsMode.ScrollWheels_TopRight);    break;
                    case 0x40: TryEmitScroll(ScrollWheelsMode.ScrollWheels_BottomRight); break;
                }
                break;

            case InputCommandMode.KeyPress:
            {
                var x = report[2];
                var y = report[3];
                var layout = _layout == KeebLayout.ISO ? KeebLayoutTables.ISOLayout : KeebLayoutTables.ANSILayout;
                foreach (var model in layout)
                {
                    if (model.FwMatrixPosition == MatrixPosition.Empty) continue;
                    if (model.FwMatrixPosition.X == x && model.FwMatrixPosition.Y == y)
                    {
                        KeyPressInvoke?.Invoke(model);
                        break;
                    }
                }
                break;
            }

            case InputCommandMode.SoftwareMode:
                if (report[3] == 0x01)
                {
                    SoftwareKeyInvoke?.Invoke(report[2]);
                }
                break;

            case InputCommandMode.Profile:
                _profileIndex = report[2];
                if (_isWaitingToGetProfileIndex) _isWaitingToGetProfileIndex = false;
                break;
        }
    }

    private void TryEmitScroll(ScrollWheelsMode mode)
    {
        var now = Environment.TickCount64;
        if (_lastScrollwheelMode != mode || (now - _lastScrollTickMs) >= _scrollInputThrottleMs)
        {
            _lastScrollwheelMode = mode;
            _lastScrollTickMs = now;
            ScrollWheelInvoke?.Invoke(mode);
        }
    }

    public void StopReadingCallback()
    {
        lock (_lock)
        {
            _cancelSource?.Cancel();
            _cancelSource?.Dispose();
            _cancelSource = null;
        }
    }

    #endregion

    #region IDisposable

    public void Dispose()
    {
        StopReadingCallback();
        try { _readingTask?.Wait(500); } catch { /* ignored */ }
        _feature.Dispose();
        _input.Dispose();
    }

    #endregion

    #region HID helpers (the legacy logs+swallows; we do the same so a transient firmware glitch doesn't kill the process)

    private void SetFeatureSafe(byte[] commands, string what)
    {
        try { _feature.SetFeature(commands); } catch { /* log silently — matches legacy */ }
    }

    private void ReadSafe(byte[] output, string what)
    {
        try { _feature.Read(output, 1000); } catch { /* see above */ }
    }

    private void WriteSafe(byte[] data, string what)
    {
        try { _feature.Write(data); } catch { /* see above */ }
    }

    private static void Sleep(int ms) => Thread.Sleep(ms);

    #endregion
}

/// <summary>Input-pipe command discriminator. Byte [1] of every 8-byte input report.</summary>
public enum InputCommandMode : byte
{
    Scrollwheel = 0x09,
    Profile = 0x02,
    SoftwareMode = 0xF1,
    KeyPress = 0xFA,
}

/// <summary>Which scroll-wheel direction/button fired (from the firmware's perspective).</summary>
public enum ScrollWheelsMode : byte
{
    NA = 0,
    ScrollWheels_TopLeft,
    ScrollWheels_BottomLeft,
    ScrollWheels_TopRight,
    ScrollWheels_BottomRight,
    ScrollWheels_Button,
}
