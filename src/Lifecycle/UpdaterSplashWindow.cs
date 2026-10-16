#if WINDOWS
using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Nexus.Service.Lifecycle;

/// <summary>
/// The "Updating Nexus" window shown while the installer replaces the app. The
/// installer kills every Nexus.exe under the install folder (and the registered
/// service image), including the helper that would host a window, so the splash
/// runs from a copy of the exe named NexusUpdater.exe outside the install folder:
/// a different image name and path, which the installer's selection leaves alone.
/// Ends on WM_CLOSE (helper startup or install failure), or a timeout.
/// </summary>
[SupportedOSPlatform("windows")]
internal static class UpdaterSplashWindow
{
    private const string CopyName = "NexusUpdater.exe";
    private const uint TimeoutMs = 300_000;
    private const int W = 330, H = 220;

    /// <summary>Copies this exe aside and starts the splash from the copy. Returns the pid, or 0.</summary>
    public static int Spawn(string fromVersion, string toVersion)
    {
        var self = Environment.ProcessPath;
        if (string.IsNullOrEmpty(self)) return 0;
        var dir = CopyDir();
        Directory.CreateDirectory(dir);
        var copy = Path.Combine(dir, CopyName);
        // A running splash locks its own copy; reusing it is fine for a window.
        try { File.Copy(self, copy, overwrite: true); }
        catch (IOException) when (File.Exists(copy)) { }
        var psi = new ProcessStartInfo(copy)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = dir,
        };
        psi.ArgumentList.Add(UpdaterSplashArgs.Flag);
        psi.ArgumentList.Add(fromVersion);
        psi.ArgumentList.Add(toVersion);
        return Process.Start(psi)?.Id ?? 0;
    }

    /// <summary>Removes the copy once no splash is running from it. Best-effort.</summary>
    public static void TryDeleteCopy()
    {
        try
        {
            var copy = Path.Combine(CopyDir(), CopyName);
            if (File.Exists(copy)) File.Delete(copy);
        }
        catch { /* still running or locked; the next update overwrites it */ }
    }

    private static string CopyDir() => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Nexus", "updater");

    // Colors are COLORREF (0x00BBGGRR). Dark and light match the old page palette.
    private readonly record struct Palette(uint Bg, uint Title, uint Ver, uint Bar, uint Shine, uint Note);

    private static readonly Palette Dark = new(Rgb(0x20, 0x20, 0x24), Rgb(0xe8, 0xe8, 0xea), Rgb(0x9a, 0x9a, 0x9e), Rgb(0x5a, 0x5a, 0x5e), Rgb(0xa6, 0xa6, 0xaa), Rgb(0x76, 0x76, 0x7a));
    private static readonly Palette Light = new(Rgb(0xf3, 0xf3, 0xf4), Rgb(0x20, 0x20, 0x24), Rgb(0x6e, 0x6e, 0x72), Rgb(0xbc, 0xbc, 0xc0), Rgb(0x86, 0x86, 0x8a), Rgb(0x9a, 0x9a, 0x9e));

    private static uint Rgb(int r, int g, int b) => (uint)(r | (g << 8) | (b << 16));

    private static WndProcDelegate? s_proc;
    private static Palette s_pal;
    private static string s_versionLine = "";
    private static IntPtr s_icon;
    private static int s_dpi = 96;
    private static int s_tick;

    /// <summary>Entry for <c>--updater-window</c>. Blocks until the window closes.</summary>
    public static int Run(string[] args)
    {
        if (!UpdaterSplashArgs.TryParse(args, out var from, out var to)) return 1;
        // One splash at a time: a second request leaves the first on screen.
        if (FindWindow(null, UpdaterSplashArgs.Title) != IntPtr.Zero) return 0;

        SetProcessDPIAware();
        var hdc = GetDC(IntPtr.Zero);
        s_dpi = GetDeviceCaps(hdc, 88);
        ReleaseDC(IntPtr.Zero, hdc);
        s_pal = IsLightTheme() ? Light : Dark;
        s_versionLine = UpdaterSplashArgs.VersionLine(from, to);

        var inst = GetModuleHandle(null);
        s_icon = ExtractIcon(inst, Environment.ProcessPath ?? "", 0);
        s_proc = Proc;
        var cls = new WNDCLASSEX
        {
            cbSize = Marshal.SizeOf<WNDCLASSEX>(),
            lpfnWndProc = s_proc,
            lpszClassName = "NexusUpdaterSplash",
            hInstance = inst,
            hCursor = LoadCursor(IntPtr.Zero, new IntPtr(32512)),
        };
        if (RegisterClassEx(ref cls) == 0) return 1;

        int w = Scale(W), h = Scale(H);
        var x = (GetSystemMetrics(0) - w) / 2;
        var y = (GetSystemMetrics(1) - h) / 2;
        // WS_POPUP, topmost tool window: borderless and absent from the taskbar.
        var hwnd = CreateWindowEx(0x00000008 | 0x00000080, cls.lpszClassName, UpdaterSplashArgs.Title,
            unchecked((int)0x80000000), x, y, w, h, IntPtr.Zero, IntPtr.Zero, inst, IntPtr.Zero);
        if (hwnd == IntPtr.Zero) return 1;

        SetTimer(hwnd, new UIntPtr(1), 16, IntPtr.Zero);
        SetTimer(hwnd, new UIntPtr(2), TimeoutMs, IntPtr.Zero);
        ShowWindow(hwnd, 5);
        SetForegroundWindow(hwnd);
        while (GetMessage(out var msg, IntPtr.Zero, 0, 0))
        {
            TranslateMessage(ref msg);
            DispatchMessage(ref msg);
        }
        return 0;
    }

    private static int Scale(int v) => v * s_dpi / 96;

    private static bool IsLightTheme()
    {
        try
        {
            using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return key?.GetValue("AppsUseLightTheme") is int v && v == 1;
        }
        catch { return false; }
    }

    private static IntPtr Proc(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        switch (msg)
        {
            case 0x0014: // WM_ERASEBKGND
                return new IntPtr(1);
            case 0x000F: // WM_PAINT
                Paint(hwnd);
                return IntPtr.Zero;
            case 0x0113: // WM_TIMER
                if (wParam.ToInt64() == 2)
                {
                    DestroyWindow(hwnd);
                }
                else
                {
                    s_tick++;
                    InvalidateRect(hwnd, IntPtr.Zero, false);
                }
                return IntPtr.Zero;
            case 0x0010: // WM_CLOSE
                DestroyWindow(hwnd);
                return IntPtr.Zero;
            case 0x0002: // WM_DESTROY
                PostQuitMessage(0);
                return IntPtr.Zero;
        }
        return DefWindowProc(hwnd, msg, wParam, lParam);
    }

    private static void Paint(IntPtr hwnd)
    {
        var hdc = BeginPaint(hwnd, out var ps);
        int w = Scale(W), h = Scale(H);
        var mem = CreateCompatibleDC(hdc);
        var bmp = CreateCompatibleBitmap(hdc, w, h);
        var oldBmp = SelectObject(mem, bmp);

        FillColor(mem, new RECT { L = 0, T = 0, R = w, B = h }, s_pal.Bg);
        if (s_icon != IntPtr.Zero)
        {
            int iw = Scale(34), ih = Scale(36);
            DrawIconEx(mem, (w - iw) / 2, Scale(44), s_icon, iw, ih, 0, IntPtr.Zero, 3);
        }
        DrawLine(mem, "Updating Nexus", 16, 600, s_pal.Title, Scale(94), w);
        DrawLine(mem, s_versionLine, 12, 400, s_pal.Ver, Scale(122), w);

        // Track with a highlight sweeping left to right, clipped to the track.
        int tw = Scale(220), th = Scale(5);
        int tx = (w - tw) / 2, ty = Scale(152);
        FillColor(mem, new RECT { L = tx, T = ty, R = tx + tw, B = ty + th }, s_pal.Bar);
        int sw = tw * 45 / 100;
        int span = tw + sw;
        int sl = tx - sw + (s_tick * Scale(2) % span);
        var shine = new RECT { L = Math.Max(sl, tx), T = ty, R = Math.Min(sl + sw, tx + tw), B = ty + th };
        if (shine.R > shine.L) FillColor(mem, shine, s_pal.Shine);

        DrawLine(mem, "Nexus will reopen automatically.", 11, 400, s_pal.Note, Scale(172), w);

        BitBlt(hdc, 0, 0, w, h, mem, 0, 0, 0x00CC0020);
        SelectObject(mem, oldBmp);
        DeleteObject(bmp);
        DeleteDC(mem);
        EndPaint(hwnd, ref ps);
    }

    private static void FillColor(IntPtr dc, RECT r, uint color)
    {
        var brush = CreateSolidBrush(color);
        FillRect(dc, ref r, brush);
        DeleteObject(brush);
    }

    private static void DrawLine(IntPtr dc, string text, int px, int weight, uint color, int top, int width)
    {
        var font = CreateFont(-Scale(px), 0, 0, 0, weight, 0, 0, 0, 1, 0, 0, 5, 0, "Segoe UI");
        var old = SelectObject(dc, font);
        SetBkMode(dc, 1);
        SetTextColor(dc, color);
        var r = new RECT { L = 0, T = top, R = width, B = top + Scale(px * 2) };
        // DT_CENTER | DT_SINGLELINE | DT_NOPREFIX
        DrawText(dc, text, -1, ref r, 0x0001 | 0x0020 | 0x0800);
        SelectObject(dc, old);
        DeleteObject(font);
    }

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate IntPtr WndProcDelegate(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WNDCLASSEX
    {
        public int cbSize;
        public int style;
        [MarshalAs(UnmanagedType.FunctionPtr)] public WndProcDelegate lpfnWndProc;
        public int cbClsExtra, cbWndExtra;
        public IntPtr hInstance, hIcon, hCursor, hbrBackground;
        public string? lpszMenuName;
        public string lpszClassName;
        public IntPtr hIconSm;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MSG
    {
        public IntPtr hwnd;
        public uint message;
        public IntPtr wParam, lParam;
        public int time;
        public int ptX, ptY;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int L, T, R, B; }

    [StructLayout(LayoutKind.Sequential)]
    private struct PAINTSTRUCT
    {
        public IntPtr hdc;
        public int fErase;
        public RECT rcPaint;
        public int fRestore, fIncUpdate;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 32)] public byte[] rgbReserved;
    }

    [DllImport("kernel32")] private static extern IntPtr GetModuleHandle(string? name);
    [DllImport("user32", CharSet = CharSet.Unicode)] private static extern ushort RegisterClassEx(ref WNDCLASSEX cls);
    [DllImport("user32", CharSet = CharSet.Unicode)] private static extern IntPtr CreateWindowEx(int exStyle, string className, string windowName, int style, int x, int y, int w, int h, IntPtr parent, IntPtr menu, IntPtr instance, IntPtr param);
    [DllImport("user32")] private static extern IntPtr DefWindowProc(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);
    [DllImport("user32")] private static extern bool GetMessage(out MSG msg, IntPtr hwnd, uint min, uint max);
    [DllImport("user32")] private static extern bool TranslateMessage(ref MSG msg);
    [DllImport("user32")] private static extern IntPtr DispatchMessage(ref MSG msg);
    [DllImport("user32")] private static extern bool DestroyWindow(IntPtr hwnd);
    [DllImport("user32")] private static extern void PostQuitMessage(int code);
    [DllImport("user32")] private static extern bool ShowWindow(IntPtr hwnd, int cmd);
    [DllImport("user32")] private static extern bool SetForegroundWindow(IntPtr hwnd);
    [DllImport("user32")] private static extern UIntPtr SetTimer(IntPtr hwnd, UIntPtr id, uint ms, IntPtr proc);
    [DllImport("user32")] private static extern bool InvalidateRect(IntPtr hwnd, IntPtr rect, bool erase);
    [DllImport("user32")] private static extern IntPtr BeginPaint(IntPtr hwnd, out PAINTSTRUCT ps);
    [DllImport("user32")] private static extern bool EndPaint(IntPtr hwnd, ref PAINTSTRUCT ps);
    [DllImport("user32")] private static extern int FillRect(IntPtr dc, ref RECT rect, IntPtr brush);
    [DllImport("user32", CharSet = CharSet.Unicode)] private static extern int DrawText(IntPtr dc, string text, int count, ref RECT rect, uint format);
    [DllImport("user32")] private static extern bool DrawIconEx(IntPtr dc, int x, int y, IntPtr icon, int cx, int cy, uint step, IntPtr flicker, uint flags);
    [DllImport("user32")] private static extern IntPtr LoadCursor(IntPtr inst, IntPtr name);
    [DllImport("user32")] private static extern int GetSystemMetrics(int index);
    [DllImport("user32")] private static extern bool SetProcessDPIAware();
    [DllImport("user32")] private static extern IntPtr GetDC(IntPtr hwnd);
    [DllImport("user32")] private static extern int ReleaseDC(IntPtr hwnd, IntPtr dc);
    [DllImport("user32", CharSet = CharSet.Unicode, EntryPoint = "FindWindowW")] private static extern IntPtr FindWindow(string? cls, string? title);
    [DllImport("shell32", CharSet = CharSet.Unicode, EntryPoint = "ExtractIconW")] private static extern IntPtr ExtractIcon(IntPtr inst, string file, int index);
    [DllImport("gdi32")] private static extern int GetDeviceCaps(IntPtr dc, int index);
    [DllImport("gdi32")] private static extern IntPtr CreateCompatibleDC(IntPtr dc);
    [DllImport("gdi32")] private static extern IntPtr CreateCompatibleBitmap(IntPtr dc, int w, int h);
    [DllImport("gdi32")] private static extern IntPtr SelectObject(IntPtr dc, IntPtr obj);
    [DllImport("gdi32")] private static extern bool DeleteObject(IntPtr obj);
    [DllImport("gdi32")] private static extern bool DeleteDC(IntPtr dc);
    [DllImport("gdi32")] private static extern IntPtr CreateSolidBrush(uint color);
    [DllImport("gdi32")] private static extern bool BitBlt(IntPtr dst, int x, int y, int w, int h, IntPtr src, int sx, int sy, uint rop);
    [DllImport("gdi32")] private static extern int SetBkMode(IntPtr dc, int mode);
    [DllImport("gdi32")] private static extern uint SetTextColor(IntPtr dc, uint color);
    [DllImport("gdi32", CharSet = CharSet.Unicode, EntryPoint = "CreateFontW")] private static extern IntPtr CreateFont(int h, int w, int esc, int orient, int weight, uint italic, uint underline, uint strike, uint charset, uint outPrec, uint clipPrec, uint quality, uint pitch, string face);
}
#endif
