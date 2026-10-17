using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text.RegularExpressions;
using Nexus.Service.Platform.Linux;

namespace Nexus.Service.Platform.Displays;

/// <summary>
/// Rotates the Y70 on a KDE Plasma Wayland session through
/// <c>kscreen-doctor</c>. The service is a root daemon, so the tool runs with
/// the adopted session's runtime dir and Wayland socket (see
/// <see cref="LinuxSession"/>); a bare root shell aborts it. The output name
/// comes from the display topology, never a hardcoded connector. Anything that
/// is not KDE (or has no tool) fails with a reason rather than faking success.
/// </summary>
public sealed partial class LinuxDisplayOrientationProvider : IDisplayOrientationProvider
{
    private const int ToolTimeoutMs = 8000;
    private const int NoSessionExit = -3;
    private const int NoWaylandExit = -4;

    private readonly Func<string> _y70Connector;
    private readonly Func<string[], (int Exit, string Output)> _run;
    private readonly object _gate = new();
    private string? _pendingOrientation;

    public LinuxDisplayOrientationProvider(Func<string> y70Connector)
        : this(y70Connector, RunKScreenDoctor)
    {
        // A root daemon that booted before login has no session to rotate in;
        // the orientation is applied once on detection, so replay it here.
        LinuxSession.SessionAdopted += OnSessionAdopted;
    }

    internal LinuxDisplayOrientationProvider(Func<string> y70Connector, Func<string[], (int Exit, string Output)> run)
    {
        _y70Connector = y70Connector;
        _run = run;
    }

    public (bool Ok, string Error) SetY70Orientation(string orientation)
    {
        var connector = _y70Connector();
        if (string.IsNullOrEmpty(connector))
        {
            return (true, "y70 panel not attached");
        }
        return Rotate(connector, orientation);
    }

    public (bool Ok, string Error) SetDisplayOrientation(string displayId, string orientation, string coverColorHex)
        => (false, "display rotation by id is not supported on this platform");

    private (bool Ok, string Error) Rotate(string connector, string orientation)
    {
        if (ToKScreenRotation(orientation) is not { } rotation)
        {
            return (false, $"unknown orientation '{orientation}'");
        }
        lock (_gate)
        {
            var (exit, listing) = _run(["-o"]);
            if (exit == NoSessionExit)
            {
                _pendingOrientation = orientation;
                return (false, listing);
            }
            if (exit != 0)
            {
                return (false, ToolFailure(exit, listing));
            }
            _pendingOrientation = null;
            var output = SelectOutput(ParseOutputs(listing), connector);
            if (output is null)
            {
                return (false, $"output '{connector}' not reported by kscreen-doctor");
            }
            if (output.Value.Rotation == rotation.Code)
            {
                return (true, $"already {orientation}");
            }

            var (setExit, setOut) = _run([$"output.{output.Value.Name}.rotation.{rotation.Name}"]);
            if (setExit != 0)
            {
                return (false, ToolFailure(setExit, setOut));
            }
            var (checkExit, after) = _run(["-o"]);
            var applied = checkExit == 0 ? SelectOutput(ParseOutputs(after), connector) : null;
            return applied?.Rotation == rotation.Code
                ? (true, $"applied {orientation} on {output.Value.Name}")
                : (false, $"{output.Value.Name} did not rotate to {rotation.Name}");
        }
    }

    private void OnSessionAdopted()
    {
        string? pending;
        lock (_gate)
        {
            pending = _pendingOrientation;
        }
        if (pending is null)
        {
            return;
        }
        var (ok, detail) = SetY70Orientation(pending);
        Console.Error.WriteLine($"[y70-orient] session adopted, replayed '{pending}' ok={ok} detail='{detail}'");
    }

    private static string ToolFailure(int exit, string output) => exit switch
    {
        NoWaylandExit => output,
        -1 => "kscreen-doctor not found (KDE Plasma required)",
        -2 => "kscreen-doctor timed out",
        _ => $"kscreen-doctor failed (exit {exit}); KDE Plasma Wayland required",
    };

    /// <summary>
    /// Canonical orientation to kscreen's rotation name and its numeric code in
    /// <c>kscreen-doctor -o</c>. Anchored on the Y70's forced orientation:
    /// PortraitFlipped is the panel's upright mounting on Windows and is the
    /// <c>right</c> rotation on the same hardware under KDE; the rest follow.
    /// </summary>
    internal static (string Name, int Code)? ToKScreenRotation(string orientation) => orientation switch
    {
        "Landscape" => ("normal", 1),
        "Portrait" => ("left", 2),
        "LandscapeFlipped" => ("inverted", 4),
        "PortraitFlipped" => ("right", 8),
        _ => null,
    };

    internal readonly record struct KScreenOutput(string Name, int Rotation);

    /// <summary>Outputs from <c>kscreen-doctor -o</c>: an <c>Output: id NAME uuid</c> header followed by a <c>Rotation: n</c> line.</summary>
    internal static List<KScreenOutput> ParseOutputs(string text)
    {
        var outputs = new List<KScreenOutput>();
        string? name = null;
        foreach (var raw in AnsiRegex().Replace(text, "").Split('\n'))
        {
            var line = raw.Trim();
            if (line.StartsWith("Output:", StringComparison.Ordinal))
            {
                var fields = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                name = fields.Length >= 3 ? fields[2] : null;
            }
            else if (name is not null && line.StartsWith("Rotation:", StringComparison.Ordinal)
                && int.TryParse(line["Rotation:".Length..].Trim(), out var code))
            {
                outputs.Add(new KScreenOutput(name, code));
                name = null;
            }
        }
        return outputs;
    }

    internal static KScreenOutput? SelectOutput(IReadOnlyList<KScreenOutput> outputs, string connector)
    {
        foreach (var output in outputs)
        {
            if (string.Equals(output.Name, connector, StringComparison.Ordinal))
            {
                return output;
            }
        }
        return null;
    }

    [GeneratedRegex("\u001b\\[[0-9;]*m")]
    private static partial Regex AnsiRegex();

    private static (int Exit, string Output) RunKScreenDoctor(string[] args)
    {
        var runtimeDir = Environment.GetEnvironmentVariable("XDG_RUNTIME_DIR");
        if (string.IsNullOrEmpty(runtimeDir) && LinuxSession.SessionUid is { } uid)
        {
            runtimeDir = $"/run/user/{uid}";
        }
        if (LinuxSession.IsRootDaemon && LinuxSession.SessionUid is null)
        {
            return (NoSessionExit, "no graphical session yet; orientation applies at login");
        }
        var wayland = Environment.GetEnvironmentVariable("WAYLAND_DISPLAY");
        if (string.IsNullOrEmpty(runtimeDir) || string.IsNullOrEmpty(wayland))
        {
            return (NoWaylandExit, "no Wayland session (KDE Plasma Wayland required)");
        }
        try
        {
            var psi = new ProcessStartInfo("kscreen-doctor")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            foreach (var a in args)
            {
                psi.ArgumentList.Add(a);
            }
            psi.Environment["XDG_RUNTIME_DIR"] = runtimeDir;
            psi.Environment["WAYLAND_DISPLAY"] = wayland;
            using var proc = Process.Start(psi);
            if (proc is null)
            {
                return (-1, "");
            }
            var stdout = proc.StandardOutput.ReadToEndAsync();
            _ = proc.StandardError.ReadToEndAsync();
            if (!proc.WaitForExit(ToolTimeoutMs))
            {
                try { proc.Kill(entireProcessTree: true); } catch { }
                return (-2, "");
            }
            return (proc.ExitCode, stdout.GetAwaiter().GetResult());
        }
        catch
        {
            return (-1, "");
        }
    }
}
