using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net.Sockets;
using System.Text;
using System.Threading;
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
    private const int MaxFullscreenTries = 3;
    private const int MaxEventConnectFailures = 5;
    private const int EventReconnectDelayMs = 2000;

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

    /// <summary>The named connector; with no connector, the one portrait strip output.</summary>
    internal static HyprlandIpc.Monitor? FindTargetMonitor(IReadOnlyList<HyprlandIpc.Monitor> monitors, string connector)
    {
        if (connector.Length > 0)
        {
            foreach (var m in monitors)
            {
                if (string.Equals(m.Name, connector, StringComparison.OrdinalIgnoreCase))
                    return m;
            }
            return null;
        }
        var strips = monitors.Where(IsPortraitStrip).ToList();
        return strips.Count == 1 ? strips[0] : null;
    }

    // Width and height are the mode's, so an odd transform (quarter turns) swaps them.
    private static bool IsPortraitStrip(HyprlandIpc.Monitor m)
    {
        if (m.Width <= 0 || m.Height <= 0)
            return false;
        var (w, h) = m.Transform % 2 == 1 ? (m.Height, m.Width) : (m.Width, m.Height);
        return h > w && h / (double)w >= MinStripAspect;
    }

    internal static Step Plan(IReadOnlyList<HyprlandIpc.Client> clients, IReadOnlyList<HyprlandIpc.Monitor> monitors, string connector, bool skipFullscreen = false)
    {
        var window = clients.Where(IsPanelWindow).Cast<HyprlandIpc.Client?>().FirstOrDefault();
        if (window is null)
            return new Step(StepKind.NoWindow);
        if (FindTargetMonitor(monitors, connector) is not { } target)
            return new Step(StepKind.NoMonitor, window.Value);
        if (window.Value.MonitorId != target.Id)
            return new Step(StepKind.Move, window.Value, target);
        var fullscreen = skipFullscreen || window.Value.Fullscreen >= 2 || window.Value.FullscreenClient >= 2;
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

    internal static string FullscreenLegacy() => "/dispatch fullscreen 0";

    internal static string FocusLua(string address)
        => $"/dispatch hl.dsp.focus({{ window = \"address:{address}\" }})";

    internal static string FocusLegacy(string address)
        => $"/dispatch focuswindow address:{address}";

    /// <summary>Starts placement, touch binding and reload re-binding; null unless a Hyprland instance answers. Dispose to stop.</summary>
    internal static IDisposable? Start(string connector)
    {
        if (HyprlandIpc.FindSocket() is not { } socketPath)
            return null;
        var cts = new CancellationTokenSource();
        var token = cts.Token;
        _ = Task.Run(async () =>
        {
            try
            {
                Func<string, string?> send = request => HyprlandIpc.Send(socketPath, request);
                Console.Error.WriteLine("[panel-kiosk] Hyprland session; placing the kiosk on the Y70 output");
                await PlaceAsync(send, connector, ms => Task.Delay(ms, token)).ConfigureAwait(false);
                MapTouch(send, connector);
                await WatchConfigReloadsAsync(socketPath, () => MapTouch(send, connector), token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[panel-kiosk] Hyprland placement failed: {ex.Message}");
            }
        });
        return new Handle(cts);
    }

    private sealed class Handle(CancellationTokenSource cts) : IDisposable
    {
        public void Dispose()
        {
            try { cts.Cancel(); }
            finally { cts.Dispose(); }
        }
    }

    private static void MapTouch(Func<string, string?> send, string connector)
        => LinuxHyprlandTouchMap.Apply(send, connector, LinuxInputDevices.ReadY70TouchNames(), LinuxHyprlandTouchMap.ReadUserConfigText());

    /// <summary>Runtime device config is dropped by a config reload; re-apply on each reload event. Gives up after repeated connect failures.</summary>
    internal static async Task WatchConfigReloadsAsync(string requestSocket, Action onReload, CancellationToken token)
    {
        var failures = 0;
        while (!token.IsCancellationRequested && failures < MaxEventConnectFailures)
        {
            try
            {
                using var socket = HyprlandIpc.Connect(HyprlandIpc.EventSocketPath(requestSocket));
                socket.ReceiveTimeout = 0;
                failures = 0;
                var pending = "";
                var buffer = new byte[4096];
                while (true)
                {
                    var n = await socket.ReceiveAsync(buffer.AsMemory(), SocketFlags.None, token).ConfigureAwait(false);
                    if (n <= 0)
                        break;
                    foreach (var line in HyprlandIpc.TakeLines(ref pending, Encoding.UTF8.GetString(buffer, 0, n)))
                    {
                        if (HyprlandIpc.IsConfigReloaded(line))
                            onReload();
                    }
                }
            }
            catch (Exception)
            {
                if (token.IsCancellationRequested)
                    return;
                failures++;
            }
            await Task.Delay(EventReconnectDelayMs, token).ConfigureAwait(false);
        }
    }

    internal static async Task<bool> PlaceAsync(Func<string, string?> send, string connector, Func<int, Task> delay)
    {
        // Captured before the kiosk maps: a new window takes focus and the move is silent.
        var previousFocus = HyprlandIpc.ParseActiveAddress(send("j/activewindow"));
        var fullscreenTries = 0;
        (int X, int Y)? cursor = null;
        for (var attempt = 0; attempt < PollAttempts; attempt++)
        {
            var step = Plan(HyprlandIpc.ParseClients(send("j/clients")), HyprlandIpc.ParseMonitors(send("j/monitors")), connector,
                skipFullscreen: fullscreenTries >= MaxFullscreenTries);
            switch (step.Kind)
            {
                case StepKind.Done:
                    RestoreFocus(send, step.Window.Address, previousFocus);
                    if (cursor is { } c)
                        send($"/dispatch movecursor {c.X} {c.Y}");
                    Console.Error.WriteLine(fullscreenTries >= MaxFullscreenTries
                        ? $"[panel-kiosk] Hyprland kiosk on {connector}; fullscreen was not confirmed"
                        : $"[panel-kiosk] Hyprland kiosk fullscreen on {connector}");
                    return true;
                case StepKind.Move when IsAddress(step.Window.Address) && WorkspaceSelector(step.Target) is { } workspace:
                    Dispatch(send, MoveLua(step.Window.Address, workspace), MoveLegacy(step.Window.Address, workspace));
                    break;
                case StepKind.Fullscreen when IsAddress(step.Window.Address):
                    fullscreenTries++;
                    if (!HyprlandIpc.IsOk(send(FullscreenLua(step.Window.Address))))
                    {
                        // hyprlang fullscreen toggles the focused window, so it runs only once the panel is confirmed focused.
                        cursor ??= HyprlandIpc.ParseCursor(send("j/cursorpos"));
                        if (HyprlandIpc.IsOk(send(FocusLegacy(step.Window.Address)))
                            && HyprlandIpc.ParseActiveAddress(send("j/activewindow")) == step.Window.Address)
                        {
                            send(FullscreenLegacy());
                        }
                    }
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
