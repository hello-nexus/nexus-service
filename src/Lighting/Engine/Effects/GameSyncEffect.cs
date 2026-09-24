using System;
using Nexus.Service.Lighting.Engine;

namespace Nexus.Service.Lighting.Engine.Effects;

// Holds the latest per-device Chroma grid and writes it to Nexus frames each tick.
// Keyboard frames: sampled per-LED from the Chroma grid when the device frame carries
// per-LED U/V (LedU/LedV populated and length == LedCount); otherwise the frame keeps
// the canvas-sampled color from SampleDevicesFromCanvas.
// Semantic peripherals (mouse, mousepad, headset): filled with the dominant grid color.
// All other devices: canvas-paint path via SampleDevicesFromCanvas.
// The shim posts at game rate; the engine consumes at its own cadence. Latest frame wins.
public sealed class GameSyncEffect : IEffect
{
    // CHROMA_CUSTOM grid: 6 rows x 22 cols. CHROMA_CUSTOM2: 8 rows x 24 cols.
    private const int CustomRows = 6;
    private const int CustomCols = 22;

    public string Name => "gamesync";

    private readonly object _lock = new();

    private DateTimeOffset? _lastFrameAtUtc;
    private string? _activeApp;

    // Latest keyboard grid: R,G,B triples row-major. Null = no frame received.
    private byte[]? _keyboardRgb;
    private int _keyboardRows;
    private int _keyboardCols;

    // Per-device archetype grids for semantic peripherals (mouse, mousepad, headset, keypad, chromalink).
    // Stored as dominant R,G,B. Null entry = no frame received for that archetype.
    private byte[]? _mouseRgb;
    private byte[]? _mousepadRgb;
    private byte[]? _headsetRgb;
    private byte[]? _keypadRgb;
    private byte[]? _chromalinkRgb;

    // Dominant color from whatever was last seen for canvas fallback.
    private byte _fallbackR, _fallbackG, _fallbackB;
    private bool _hasFrame;

    /// <summary>Unix epoch milliseconds of the last ingested frame. Null when no frame has been received this session.</summary>
    public long? LastFrameAtMs
    {
        get
        {
            lock (_lock) { return _lastFrameAtUtc?.ToUnixTimeMilliseconds(); }
        }
    }

    /// <summary>Source application title from the most recent frame that carried one. Null when unknown.</summary>
    public string? ActiveApp
    {
        get
        {
            lock (_lock) { return _activeApp; }
        }
    }

    /// <summary>Clears per-session state. Called when Game Sync is (re)started.</summary>
    public void Reset()
    {
        lock (_lock)
        {
            _lastFrameAtUtc = null;
            _activeApp = null;
        }
    }

    // Ingest one Chroma frame from the shim. Thread-safe. Called from the route handler.
    // colors is COLORREF 0x00BBGGRR packed: R=low byte, G=next, B=high byte of the low 3 bytes.
    public void IngestFrame(string device, string effect, int rows, int cols, int[] colors, string app = "")
    {
        var effectUpper = (effect ?? "").ToUpperInvariant();
        var deviceLower = (device ?? "").ToLowerInvariant();

        if (effectUpper == "CHROMA_NONE" || colors.Length == 0)
        {
            lock (_lock)
            {
                _hasFrame = true;
                _lastFrameAtUtc = DateTimeOffset.UtcNow;
                if ((app ?? "").Length > 0) { _activeApp = app; }
                ClearDevice(deviceLower);
            }
            return;
        }

        if (effectUpper == "CHROMA_STATIC" && colors.Length >= 1)
        {
            var cr = colors[0];
            var r = (byte)(cr & 0xFF);
            var g = (byte)((cr >> 8) & 0xFF);
            var b = (byte)((cr >> 16) & 0xFF);
            lock (_lock)
            {
                _hasFrame = true;
                _lastFrameAtUtc = DateTimeOffset.UtcNow;
                if ((app ?? "").Length > 0) { _activeApp = app; }
                SetDeviceSolid(deviceLower, r, g, b);
                _fallbackR = r; _fallbackG = g; _fallbackB = b;
            }
            return;
        }

        // CHROMA_CUSTOM / CHROMA_CUSTOM2: row-major grid.
        var gridRows = rows > 0 ? rows : CustomRows;
        var gridCols = cols > 0 ? cols : CustomCols;
        var cellCount = gridRows * gridCols;
        var rgb = new byte[cellCount * 3];
        var limit = Math.Min(cellCount, colors.Length);
        for (int i = 0; i < limit; i++)
        {
            var cr = colors[i];
            rgb[i * 3] = (byte)(cr & 0xFF);
            rgb[i * 3 + 1] = (byte)((cr >> 8) & 0xFF);
            rgb[i * 3 + 2] = (byte)((cr >> 16) & 0xFF);
        }

        lock (_lock)
        {
            _hasFrame = true;
            _lastFrameAtUtc = DateTimeOffset.UtcNow;
            if ((app ?? "").Length > 0) { _activeApp = app; }
            if (deviceLower == "keyboard")
            {
                _keyboardRgb = rgb;
                _keyboardRows = gridRows;
                _keyboardCols = gridCols;
            }
            else
            {
                var (dr, dg, db) = DominantColor(rgb, limit);
                SetDeviceSolid(deviceLower, dr, dg, db);
            }
            var (fr, fg, fb) = DominantColor(rgb, limit);
            _fallbackR = fr; _fallbackG = fg; _fallbackB = fb;
        }
    }

    // Called by the engine each tick. Paints the canvas for archetype-less devices.
    // Keyboard and semantic-peripheral frames are handled by WriteToDevices, which
    // the engine calls after SampleDevicesFromCanvas.
    public void RenderFrame(CanvasBuffer canvas, double tickMs)
    {
        byte fr, fg, fb;
        bool hasFrame;
        lock (_lock)
        {
            fr = _fallbackR; fg = _fallbackG; fb = _fallbackB;
            hasFrame = _hasFrame;
        }
        canvas.Fill(hasFrame ? fr : (byte)0, hasFrame ? fg : (byte)0, hasFrame ? fb : (byte)0);
    }

    // Called by the engine after SampleDevicesFromCanvas. Overwrites LEDs on frames
    // with a known archetype: keyboards get per-LED grid sampling; semantic peripherals
    // get a solid dominant fill. Frames with null Archetype keep the canvas-sampled color.
    // `skip`, when given, marks devices an overlay already painted (a locked Static
    // look, a highlight, a test pattern); those keep what the overlay gave them.
    public void WriteToDevices(DeviceFrame[] devices, bool[]? skip = null)
    {
        byte[]? kbRgb;
        int kbRows, kbCols;
        byte[]? mouseRgb, mousepadRgb, headsetRgb, keypadRgb, chromalinkRgb;

        lock (_lock)
        {
            if (!_hasFrame)
            {
                return;
            }

            kbRgb = _keyboardRgb;
            kbRows = _keyboardRows;
            kbCols = _keyboardCols;
            mouseRgb = _mouseRgb;
            mousepadRgb = _mousepadRgb;
            headsetRgb = _headsetRgb;
            keypadRgb = _keypadRgb;
            chromalinkRgb = _chromalinkRgb;
        }

        for (var di = 0; di < devices.Length; di++)
        {
            if (skip is not null && di < skip.Length && skip[di]) continue;
            var frame = devices[di];
            switch (frame.Archetype)
            {
                case "keyboard":
                    if (kbRgb is not null && kbRows > 0 && kbCols > 0
                        && frame.LedU is { } lu && frame.LedV is { } lv
                        && lu.Length == frame.LedCount && lv.Length == frame.LedCount)
                    {
                        WriteKeyboardGrid(frame, kbRgb, kbRows, kbCols, lu, lv);
                    }
                    break;
                case "mouse":
                    FillFrameIfData(frame, mouseRgb);
                    break;
                case "mousepad":
                    FillFrameIfData(frame, mousepadRgb);
                    break;
                case "headset":
                    FillFrameIfData(frame, headsetRgb);
                    break;
                case "keypad":
                    FillFrameIfData(frame, keypadRgb);
                    break;
                case "chromalink":
                    FillFrameIfData(frame, chromalinkRgb);
                    break;
                // null Archetype: keep canvas-sampled color (SampleDevicesFromCanvas already ran).
            }
        }
    }

    // U maps to grid column, V maps to grid row.
    private static void WriteKeyboardGrid(DeviceFrame frame, byte[] rgb, int rows, int cols,
        float[] ledU, float[] ledV)
    {
        var disabledArr = frame.LedDisabled;
        for (int i = 0; i < frame.LedCount; i++)
        {
            if (disabledArr is not null && i < disabledArr.Length && disabledArr[i])
            {
                frame.SetLed(i, 0, 0, 0);
                continue;
            }
            var col = Math.Clamp((int)MathF.Round(ledU[i] * (cols - 1)), 0, cols - 1);
            var row = Math.Clamp((int)MathF.Round(ledV[i] * (rows - 1)), 0, rows - 1);
            var off = (row * cols + col) * 3;
            frame.SetLed(i, rgb[off], rgb[off + 1], rgb[off + 2]);
        }
    }

    private static void FillFrameIfData(DeviceFrame frame, byte[]? solidRgb)
    {
        if (solidRgb is null || solidRgb.Length < 3)
        {
            return;
        }

        frame.Fill(solidRgb[0], solidRgb[1], solidRgb[2]);
    }

    private static (byte r, byte g, byte b) DominantColor(byte[] rgb, int limit)
    {
        long sumR = 0, sumG = 0, sumB = 0, count = 0;
        for (int i = 0; i < limit; i++)
        {
            var r = rgb[i * 3]; var g = rgb[i * 3 + 1]; var b = rgb[i * 3 + 2];
            if (r > 0 || g > 0 || b > 0)
            {
                sumR += r; sumG += g; sumB += b; count++;
            }
        }
        return count > 0 ? ((byte)(sumR / count), (byte)(sumG / count), (byte)(sumB / count)) : ((byte)0, (byte)0, (byte)0);
    }

    private void ClearDevice(string deviceLower)
    {
        switch (deviceLower)
        {
            case "keyboard": _keyboardRgb = null; break;
            case "mouse": _mouseRgb = null; break;
            case "mousepad": _mousepadRgb = null; break;
            case "headset": _headsetRgb = null; break;
            case "keypad": _keypadRgb = null; break;
            case "chromalink": _chromalinkRgb = null; break;
        }
    }

    private void SetDeviceSolid(string deviceLower, byte r, byte g, byte b)
    {
        var solid = new byte[] { r, g, b };
        switch (deviceLower)
        {
            // CHROMA_STATIC keyboard: clear grid so WriteToDevices skips it; canvas fallback applies.
            case "keyboard": _keyboardRgb = null; break;
            case "mouse": _mouseRgb = solid; break;
            case "mousepad": _mousepadRgb = solid; break;
            case "headset": _headsetRgb = solid; break;
            case "keypad": _keypadRgb = solid; break;
            case "chromalink": _chromalinkRgb = solid; break;
        }
    }

    // Sets a whole-rig solid color from GSI state; clears per-device grids so canvas fill drives all devices.
    public void IngestAuthoredFill(byte r, byte g, byte b, string app)
    {
        lock (_lock)
        {
            _hasFrame = true;
            _lastFrameAtUtc = DateTimeOffset.UtcNow;
            if (app.Length > 0)
            {
                _activeApp = app;
            }

            _fallbackR = r;
            _fallbackG = g;
            _fallbackB = b;
            _keyboardRgb = null;
            _mouseRgb = null;
            _mousepadRgb = null;
            _headsetRgb = null;
            _keypadRgb = null;
            _chromalinkRgb = null;
        }
    }

    public void Dispose() { }
}
