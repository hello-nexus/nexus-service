using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;

namespace Nexus.Service.Platform.Linux;

/// <summary>
/// Moves the Y70 kiosk onto the Y70 output under Hyprland, silently and without
/// taking focus. Dispatchers use the Lua form first (<c>dispatch</c> wraps its
/// argument in <c>hl.dispatch</c> under a Lua config) and the hyprlang form when
/// the Lua one is rejected. Other compositors never reach it: no socket answers.
/// </summary>
internal static class LinuxHyprlandPanelPlacement
{
    /// <summary>Wayland app_id Chromium gets from the kiosk URL; Chromium ignores --class there.</summary>
    internal const string ChromiumClassSuffix = "-localhost__panel-Default";

    private const int PollAttempts = 40;
    private const int PollIntervalMs = 500;
    private const double MinStripAspect = 3.0;

    internal enum StepKind { NoWindow, NoMonitor, Done, Move, Fullscreen }

    internal readonly record struct Step(StepKind Kind, HyprlandIpc.Client Window = default, HyprlandIpc.Monitor Target = default);

    internal static bool IsPanelWindow(HyprlandIpc.Client c)
    {
        if (!c.Mapped)
            return false;
        return IsPanelClass(c.Class) || IsPanelClass(c.InitialClass);
    }

    private static bool IsPanelClass(string cls)
        => string.Equals(cls, LinuxKWinPanelPlacement.FirefoxClass, StringComparison.Ordinal)
           || cls.EndsWith(ChromiumClassSuffix, StringComparison.Ordinal);

    /// <summary>The named connector, else the one strip-shaped output (either orientation), else none.</summary>
    internal static HyprlandIpc.Monitor? FindTargetMonitor(IReadOnlyList<HyprlandIpc.Monitor> monitors, string connector)
    {
        if (connector.Length > 0)
        {
            foreach (var m in monitors)
            {
                if (string.Equals(m.Name, connector, StringComparison.OrdinalIgnoreCase))
                    return m;
            }
        }
        var strips = monitors.Where(m => m.Width > 0 && m.Height > 0
            && Math.Max(m.Width, m.Height) / (double)Math.Min(m.Width, m.Height) >= MinStripAspect).ToList();
        return strips.Count == 1 ? strips[0] : null;
    }

    internal static Step Plan(IReadOnlyList<HyprlandIpc.Client> clients, IReadOnlyList<HyprlandIpc.Monitor> monitors, string connector)
    {
        var window = clients.Where(IsPanelWindow).Cast<HyprlandIpc.Client?>().FirstOrDefault();
        if (window is null)
            return new Step(StepKind.NoWindow);
        if (FindTargetMonitor(monitors, connector) is not { } target)
            return new Step(StepKind.NoMonitor, window.Value);
        if (window.Value.MonitorId != target.Id)
            return new Step(StepKind.Move, window.Value, target);
        var fullscreen = window.Value.Fullscreen >= 2 || window.Value.FullscreenClient >= 2;
        return new Step(fullscreen ? StepKind.Done : StepKind.Fullscreen, window.Value, target);
    }

    /// <summary>Hyprland window addresses are lowercase hex with a 0x prefix.</summary>
    internal static bool IsAddress(string address)
        => address.Length > 2 && address.StartsWith("0x", StringComparison.Ordinal) && address.Skip(2).All(Uri.IsHexDigit);

    internal static string? WorkspaceSelector(HyprlandIpc.Monitor target)
    {
        if (target.WorkspaceId is { } id)
            return id.ToString(CultureInfo.InvariantCulture);
        return target.WorkspaceName.Length > 0 && target.WorkspaceName.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.')
            ? "name:" + target.WorkspaceName
            : null;
    }

    internal static string MoveLua(string address, string workspace)
        => $"/dispatch hl.dsp.window.move({{ workspace = \"{workspace}\", follow = false, window = \"address:{address}\" }})";

    internal static string MoveLegacy(string address, string workspace)
        => $"/dispatch movetoworkspacesilent {workspace},address:{address}";

    internal static string FullscreenLua(string address)
        => $"/dispatch hl.dsp.window.fullscreen({{ mode = \"fullscreen\", action = \"set\", window = \"address:{address}\" }})";

    internal static string FocusLua(string address)
        => $"/dispatch hl.dsp.focus({{ window = \"address:{address}\" }})";

    internal static string FocusLegacy(string address)
        => $"/dispatch focuswindow address:{address}";

    /// <summary>Fire and forget from the kiosk spawn; a no-op unless a Hyprland instance answers.</summary>
    internal static void Start(string connector)
    {
        _ = Task.Run(async () =>
        {
            try
            {
                if (HyprlandIpc.Open() is not { } send)
                    return;
                Console.Error.WriteLine("[panel-kiosk] Hyprland session; placing the kiosk on the Y70 output");
                await PlaceAsync(send, connector, Task.Delay).ConfigureAwait(false);
                LinuxHyprlandTouchMap.Apply(send, connector, LinuxInputDevices.ReadY70TouchNames(), LinuxHyprlandTouchMap.ReadUserConfigText());
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[panel-kiosk] Hyprland placement failed: {ex.Message}");
            }
        });
    }

    internal static async Task<bool> PlaceAsync(Func<string, string?> send, string connector, Func<int, Task> delay)
    {
        // Captured before the kiosk maps: a new window takes focus and the move is silent.
        var previousFocus = HyprlandIpc.ParseActiveAddress(send("j/activewindow"));
        for (var attempt = 0; attempt < PollAttempts; attempt++)
        {
            var step = Plan(HyprlandIpc.ParseClients(send("j/clients")), HyprlandIpc.ParseMonitors(send("j/monitors")), connector);
            switch (step.Kind)
            {
                case StepKind.Done:
                    RestoreFocus(send, step.Window.Address, previousFocus);
                    Console.Error.WriteLine($"[panel-kiosk] Hyprland kiosk fullscreen on {connector}");
                    return true;
                case StepKind.Move when IsAddress(step.Window.Address) && WorkspaceSelector(step.Target) is { } workspace:
                    Dispatch(send, MoveLua(step.Window.Address, workspace), MoveLegacy(step.Window.Address, workspace));
                    break;
                case StepKind.Fullscreen when IsAddress(step.Window.Address):
                    Dispatch(send, FullscreenLua(step.Window.Address), null);
                    break;
            }
            await delay(PollIntervalMs).ConfigureAwait(false);
        }
        Console.Error.WriteLine($"[panel-kiosk] Hyprland kiosk did not settle on {connector}");
        return false;
    }

    private static void Dispatch(Func<string, string?> send, string lua, string? legacy)
    {
        if (HyprlandIpc.IsOk(send(lua)))
            return;
        if (legacy is not null)
            send(legacy);
    }

    private static void RestoreFocus(Func<string, string?> send, string panelAddress, string? previousFocus)
    {
        if (previousFocus is null || !IsAddress(previousFocus) || previousFocus == panelAddress)
            return;
        if (HyprlandIpc.ParseActiveAddress(send("j/activewindow")) != panelAddress)
            return;
        if (!HyprlandIpc.ParseClients(send("j/clients")).Any(c => c.Address == previousFocus && c.Mapped))
            return;
        Dispatch(send, FocusLua(previousFocus), FocusLegacy(previousFocus));
    }
}
