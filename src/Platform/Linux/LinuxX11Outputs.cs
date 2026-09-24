using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;

namespace Nexus.Service.Platform.Linux;

/// <summary>
/// X11 output geometry from <c>xrandr --query</c>, for placing a panel kiosk on
/// the monitor it belongs to. Wayland gives a client no way to choose an output
/// (hence <see cref="LinuxKWinPanelPlacement"/> on KDE); on X11 a plain
/// <c>--window-position</c> does it under any window manager.
/// </summary>
internal static class LinuxX11Outputs
{
    internal readonly record struct Output(string Name, int Width, int Height, int X, int Y);

    /// <summary>A portrait strip this much taller than wide is the Y70: a 32:9
    /// desktop monitor is that wide, never that tall. Matches the KWin script.</summary>
    private const double Y70MinAspect = 3.0;

    /// <summary>Connector name inside a <c>/displays</c> id, which is
    /// <c>{Mfg}{Product:X4}-{connector}</c> with a readable EDID and bare
    /// <c>{connector}</c> without. Connector names carry their own dashes
    /// (<c>HDMI-A-1</c>), so only an exact-shape prefix is dropped.</summary>
    internal static string ConnectorFromDisplayId(string? displayId)
    {
        if (string.IsNullOrEmpty(displayId))
            return "";
        var dash = displayId.IndexOf('-');
        if (dash != 7)
            return displayId;
        for (var i = 0; i < 3; i++)
        {
            if (displayId[i] is < 'A' or > 'Z')
                return displayId;
        }
        for (var i = 3; i < 7; i++)
        {
            if (!Uri.IsHexDigit(displayId[i]))
                return displayId;
        }
        return displayId[(dash + 1)..];
    }

    /// <summary>Connected outputs from <c>xrandr --query</c>. The geometry token is
    /// post-rotation (<c>1100x3840+3440+0</c> for a rotated panel), which is what a
    /// window position needs; unmapped outputs carry no token and are skipped.</summary>
    internal static List<Output> Parse(string xrandrQuery)
    {
        var outputs = new List<Output>();
        foreach (var raw in xrandrQuery.Split('\n'))
        {
            // Mode lines are indented; output lines start at column 0.
            if (raw.Length == 0 || char.IsWhiteSpace(raw[0]))
                continue;
            var fields = raw.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (fields.Length < 3 || !string.Equals(fields[1], "connected", StringComparison.Ordinal))
                continue;
            foreach (var field in fields)
            {
                if (TryParseGeometry(field, out var geometry))
                {
                    outputs.Add(new Output(fields[0], geometry.Width, geometry.Height, geometry.X, geometry.Y));
                    break;
                }
            }
        }
        return outputs;
    }

    // "1920x1080+0+0", never negative: X screen coordinates start at the origin.
    private static bool TryParseGeometry(string field, out (int Width, int Height, int X, int Y) geometry)
    {
        geometry = default;
        var x = field.IndexOf('x');
        var plus1 = field.IndexOf('+');
        if (x <= 0 || plus1 <= x)
            return false;
        var plus2 = field.IndexOf('+', plus1 + 1);
        if (plus2 < 0)
            return false;
        return int.TryParse(field[..x], NumberStyles.None, CultureInfo.InvariantCulture, out geometry.Width)
            && int.TryParse(field[(x + 1)..plus1], NumberStyles.None, CultureInfo.InvariantCulture, out geometry.Height)
            && int.TryParse(field[(plus1 + 1)..plus2], NumberStyles.None, CultureInfo.InvariantCulture, out geometry.X)
            && int.TryParse(field[(plus2 + 1)..], NumberStyles.None, CultureInfo.InvariantCulture, out geometry.Y);
    }

    /// <summary>The output to place a kiosk on: the named connector, else - for the
    /// Y70 only - the portrait strip. A promoted monitor gets no shape fallback,
    /// since guessing the wrong desktop screen is worse than leaving it placed.</summary>
    internal static Output? Select(IReadOnlyList<Output> outputs, string connector, bool allowPortraitFallback)
    {
        foreach (var output in outputs)
        {
            if (string.Equals(output.Name, connector, StringComparison.OrdinalIgnoreCase))
                return output;
        }
        if (!allowPortraitFallback)
            return null;
        foreach (var output in outputs)
        {
            if (output.Height > output.Width && output.Height / (double)output.Width >= Y70MinAspect)
                return output;
        }
        return null;
    }

    private static bool _unavailableLogged;

    /// <summary>Query xrandr as the session user and pick the output for
    /// <paramref name="connector"/>. Null on Wayland, without xrandr, or on no match;
    /// the caller then leaves placement to the window manager.</summary>
    public static Output? Find(string connector, bool allowPortraitFallback)
    {
        // A --user install inherits DISPLAY instead of adopting a session.
        if (LinuxSession.SessionDisplay is null
            && string.IsNullOrEmpty(Environment.GetEnvironmentVariable("DISPLAY")))
        {
            return null;
        }
        try
        {
            // stderr is inherited, not piped: nothing drains it here, and a driver
            // that warns on every query would fill the pipe and block the child.
            var psi = new ProcessStartInfo
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                CreateNoWindow = true,
            };
            // The daemon holds neither the session uid nor its display env.
            var (file, args) = LinuxSession.WrapSpawnAsSessionUser("xrandr", new List<string> { "--query" });
            psi.FileName = file;
            foreach (var a in args)
                psi.ArgumentList.Add(a);
            LinuxSession.ApplySessionDisplayEnv(psi);
            using var proc = Process.Start(psi);
            if (proc is null)
                return null;
            var stdout = proc.StandardOutput.ReadToEnd();
            if (!proc.WaitForExit(QueryTimeoutMs))
            {
                try { proc.Kill(entireProcessTree: true); } catch { }
                return null;
            }
            if (proc.ExitCode != 0)
            {
                LogUnavailableOnce($"xrandr exited {proc.ExitCode}");
                return null;
            }
            return Select(Parse(stdout), connector, allowPortraitFallback);
        }
        catch (Exception ex)
        {
            LogUnavailableOnce(ex.Message);
            return null;
        }
    }

    private static void LogUnavailableOnce(string reason)
    {
        if (_unavailableLogged)
            return;
        _unavailableLogged = true;
        Console.Error.WriteLine(
            $"[panel-kiosk] xrandr unavailable, letting the window manager place the kiosk: {reason}");
    }

    private const int QueryTimeoutMs = 5000;
}
