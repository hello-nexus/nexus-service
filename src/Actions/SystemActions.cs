using System;
using System.Diagnostics;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Nexus.Service.Activity;
using Nexus.Service.Models;
using Nexus.Service.Models.Activity;
using Nexus.Service.Models.Peripherals.Keeb;
using Nexus.Service.Peripherals.Keeb;
using Nexus.Service.Platform.Clipboard;
using Nexus.Service.Platform.Power;

namespace Nexus.Service.Actions;

/// <summary>
/// Injectable body for the OS-level actions <c>SystemRoutes.cs</c> exposes
/// over REST. Extracted so <c>DeckActionExecutor</c> can drive the same
/// providers headless (no loopback HTTP call) for a physical Stream Deck
/// press. Routes delegate here so both callers share one implementation.
/// </summary>
public sealed class SystemActions
{
    private readonly IInputterProvider _inputter;
    private readonly IClipboardProvider _clipboard;
    private readonly ISystemPowerProvider _power;
    private readonly IAudioDeviceProvider _audio;
    private readonly IVolumeProvider _volume;
    private readonly IShortcutsProvider _shortcuts;
    private readonly IServiceProvider _sp;

    public SystemActions(
        IInputterProvider inputter,
        IClipboardProvider clipboard,
        ISystemPowerProvider power,
        IAudioDeviceProvider audio,
        IVolumeProvider volume,
        IShortcutsProvider shortcuts,
        IServiceProvider sp)
    {
        _inputter = inputter;
        _clipboard = clipboard;
        _power = power;
        _audio = audio;
        _volume = volume;
        _shortcuts = shortcuts;
        _sp = sp;
    }

    /// <summary>
    /// Keystroke injection for the deck hotkey/hotkeySwitch actions and the
    /// /system/input/keys route. On Windows, a connected user-session helper
    /// injects the strokes itself: the service runs LocalSystem in Session 0,
    /// where SendInput has no interactive desktop to reach, so a direct send
    /// silently no-ops without the helper. macOS/Linux and an interactive
    /// (non-service) Windows run use the local inputter directly, since they
    /// already execute in the user's own session.
    /// </summary>
    public async Task<ApiResponse> SendKeysAsync(SendKeysBody body)
    {
        var input = BuildKeyStrokes(body);
        if (input.Strokes.Count == 0)
        {
            return ApiResponse.Fail("key or strokes required");
        }
#if WINDOWS
        if (OperatingSystem.IsWindows())
        {
            var registry = _sp.GetService<Nexus.Service.Helper.HelperRegistry>();
            if (registry?.GetAny() is not null)
            {
                var ok = await Nexus.Service.Helper.Domains.InputCommands
                    .SendKeysAsync(registry, input).ConfigureAwait(false);
                return ok ? ApiResponse.Ok() : ApiResponse.Fail("send keys failed");
            }
            if (!Environment.UserInteractive)
            {
                return ApiResponse.Fail("no interactive user session");
            }
        }
#endif
        _inputter.Send(input);
        return ApiResponse.Ok();
    }

    /// <summary>
    /// Clipboard-set then paste - the only Unicode-reliable cross-platform
    /// path. Clobbers the clipboard (restore deferred). Always pastes; there
    /// is no non-paste path. On Windows, a connected user-session helper does
    /// both steps itself: the service runs LocalSystem in Session 0, where a
    /// direct clipboard set lands on an invisible clipboard and SendInput has
    /// no interactive desktop to inject into, so both silently no-op without
    /// the helper. macOS/Linux and an interactive (non-service) Windows run
    /// use the local providers directly, since they already execute in the
    /// user's own session.
    /// </summary>
    public async Task<ApiResponse> SendTextAsync(string text)
    {
        if (text.Length == 0)
        {
            return ApiResponse.Fail("text required");
        }
#if WINDOWS
        if (OperatingSystem.IsWindows())
        {
            var registry = _sp.GetService<Nexus.Service.Helper.HelperRegistry>();
            if (registry?.GetAny() is not null)
            {
                var ok = await Nexus.Service.Helper.Domains.ClipboardCommands
                    .SetTextAndPasteAsync(registry, text).ConfigureAwait(false);
                return ok ? ApiResponse.Ok() : ApiResponse.Fail("paste failed");
            }
            if (!Environment.UserInteractive)
            {
                return ApiResponse.Fail("no interactive user session");
            }
        }
#endif
        if (!_clipboard.SetText(text))
        {
            return ApiResponse.Fail("clipboard unavailable");
        }
        _inputter.Send(PasteChord());
        return ApiResponse.Ok();
    }

    public async Task<ApiResponse> OpenSettingsAsync()
    {
        try
        {
            if (OperatingSystem.IsWindows())
            {
#if WINDOWS
                var registry = _sp.GetService<Nexus.Service.Helper.HelperRegistry>();
                if (registry?.GetAny() is not null)
                {
                    await Nexus.Service.Helper.Domains.SystemCommands.OpenSettingsAsync(registry).ConfigureAwait(false);
                    return ApiResponse.Ok("opened");
                }
                if (!Environment.UserInteractive)
                {
                    return ApiResponse.Fail("no interactive user session");
                }
#endif
                Process.Start(new ProcessStartInfo("ms-settings:") { UseShellExecute = true });
                return ApiResponse.Ok("opened");
            }
            if (OperatingSystem.IsMacOS())
            {
                var exit = Nexus.Service.Platform.ShellExecutor.RunExit("open", 5000, "-b", "com.apple.systempreferences");
                return exit == 0 ? ApiResponse.Ok("opened") : ApiResponse.Fail("failed to open settings");
            }
            string[] linuxCandidates = ["gnome-control-center", "systemsettings5", "systemsettings"];
            foreach (var candidate in linuxCandidates)
            {
#if LINUX
                // setpriv (the session-user wrapper below) exists even when the
                // wrapped candidate does not, so Process.Start would "succeed" on
                // a missing candidate - probe PATH first to fall through properly.
                if (!LinuxToolExists(candidate))
                {
                    continue;
                }
#endif
                try
                {
#if LINUX
                    // Root daemon: the settings app must run in the session user's
                    // context, not root's.
                    var (file, args) = Nexus.Service.Platform.Linux.LinuxSession.WrapSpawnAsSessionUser(
                        candidate, new List<string>());
                    var psi = new ProcessStartInfo(file) { UseShellExecute = false };
                    foreach (var a in args)
                        psi.ArgumentList.Add(a);
#else
                    var psi = new ProcessStartInfo(candidate) { UseShellExecute = false };
#endif
                    Process.Start(psi);
                    return ApiResponse.Ok("opened");
                }
                catch { /* launcher not installed - try the next */ }
            }
            return ApiResponse.Fail("no settings app found");
        }
        catch (Exception ex)
        {
            return ApiResponse.Fail($"failed to open settings: {ex.Message}");
        }
    }

    public async Task<ApiResponse> OpenUrlAsync(string url)
    {
        var trimmed = url?.Trim() ?? "";
        if (string.IsNullOrEmpty(trimmed))
        {
            return ApiResponse.Fail("url is required");
        }
        if (!Uri.TryCreate(trimmed, UriKind.Absolute, out var parsed) ||
            (parsed.Scheme != "http" && parsed.Scheme != "https"))
        {
            return ApiResponse.Fail("invalid url - must be an absolute http or https URL");
        }

        return await LaunchUrlAsync(parsed.AbsoluteUri).ConfigureAwait(false);
    }

    /// <summary>
    /// Hands an absolute URI to the user session's default handler. The
    /// caller owns the scheme allowlist, so a caller that permits more than
    /// http/https (the SDK openUrl host action) shares this launch path
    /// without widening what the REST route accepts.
    /// </summary>
    internal async Task<ApiResponse> LaunchUrlAsync(string absoluteUri)
    {
        try
        {
#if WINDOWS
            if (OperatingSystem.IsWindows())
            {
                var registry = _sp.GetService<Nexus.Service.Helper.HelperRegistry>();
                if (registry?.GetAny() is not null)
                {
                    await Nexus.Service.Helper.Domains.SystemCommands.OpenUrlAsync(registry, absoluteUri).ConfigureAwait(false);
                    return ApiResponse.Ok("opened");
                }
                if (!Environment.UserInteractive)
                {
                    return ApiResponse.Fail("no interactive user session");
                }
            }
#endif
            await Task.CompletedTask.ConfigureAwait(false);
#if LINUX
            if (OperatingSystem.IsLinux())
            {
                // UseShellExecute's URL handoff has no session-aware spawn point to
                // wrap, so a root daemon must invoke xdg-open explicitly instead.
                var (file, args) = Nexus.Service.Platform.Linux.LinuxSession.WrapSpawnAsSessionUser(
                    "xdg-open", new List<string> { absoluteUri });
                var psi = new ProcessStartInfo(file) { UseShellExecute = false };
                foreach (var a in args)
                    psi.ArgumentList.Add(a);
                Process.Start(psi);
            }
            else
#endif
            {
                Process.Start(new ProcessStartInfo(absoluteUri) { UseShellExecute = true });
            }
            return ApiResponse.Ok("opened");
        }
        catch (Exception ex)
        {
            return ApiResponse.Fail($"failed to open url: {ex.Message}");
        }
    }

    /// <summary>
    /// Opens the OS capture UI for the deck Screen Shot / Screen Record keys.
    /// Windows hands the Snipping Tool capture or record overlay URI to the
    /// user session; macOS runs an interactive screencapture for a still and
    /// opens the Screenshot toolbar for a recording; Linux tries the common
    /// capture tools for a still and has no recorder to open.
    /// </summary>
    public async Task<ApiResponse> OpenScreenCaptureAsync(bool record)
    {
        if (OperatingSystem.IsWindows())
        {
            return await LaunchUrlAsync(record ? "ms-screenclip://capture/video" : "ms-screenclip://capture/image").ConfigureAwait(false);
        }
        try
        {
            if (OperatingSystem.IsMacOS())
            {
                if (record)
                {
                    return Nexus.Service.Platform.ShellExecutor.RunExit("open", 5000, "-a", "Screenshot") == 0
                        ? ApiResponse.Ok("opened")
                        : ApiResponse.Fail("failed to open Screenshot");
                }
                var psi = new ProcessStartInfo("/usr/sbin/screencapture") { UseShellExecute = false };
                psi.ArgumentList.Add("-i");
                psi.ArgumentList.Add(MacScreenshotPath());
                Process.Start(psi);
                return ApiResponse.Ok("opened");
            }
#if LINUX
            if (OperatingSystem.IsLinux())
            {
                string[][] candidates = record
                    ? Array.Empty<string[]>()
                    : [["gnome-screenshot", "-i"], ["spectacle", "-r"], ["flameshot", "gui"]];
                foreach (var candidate in candidates)
                {
                    if (!LinuxToolExists(candidate[0]))
                    {
                        continue;
                    }
                    try
                    {
                        var (file, args) = Nexus.Service.Platform.Linux.LinuxSession.WrapSpawnAsSessionUser(
                            candidate[0], new List<string>(candidate[1..]));
                        var linuxPsi = new ProcessStartInfo(file) { UseShellExecute = false };
                        foreach (var a in args)
                            linuxPsi.ArgumentList.Add(a);
                        Process.Start(linuxPsi);
                        return ApiResponse.Ok("opened");
                    }
                    catch { /* launcher not installed - try the next */ }
                }
            }
#endif
            var message = record ? "no screen recorder available on this platform" : "no screenshot tool found";
            Nexus.Service.Platform.ServiceLog.Warn($"[system-actions] {message}");
            return ApiResponse.Fail(message);
        }
        catch (Exception ex)
        {
            return ApiResponse.Fail($"failed to open screen capture: {ex.Message}");
        }
    }

    // macOS's own naming, in the configured screenshot folder or the Desktop.
    private static string MacScreenshotPath()
    {
        var configured = Nexus.Service.Platform.ShellExecutor.Run("defaults", "read", "com.apple.screencapture", "location").Trim();
        var folder = !string.IsNullOrEmpty(configured) && System.IO.Directory.Exists(configured)
            ? configured
            : Environment.GetFolderPath(Environment.SpecialFolder.Desktop);
        return System.IO.Path.Combine(folder, $"Screenshot {DateTime.Now:yyyy-MM-dd} at {DateTime.Now:HH.mm.ss}.png");
    }

    /// <summary>LAN-only in the route (denied on the relay) - opens arbitrary local files.</summary>
    public async Task<ApiResponse> OpenPathAsync(string path)
    {
        var trimmed = path?.Trim() ?? "";
        if (string.IsNullOrEmpty(trimmed))
        {
            return ApiResponse.Fail("path is required");
        }
        if (!File.Exists(trimmed) && !Directory.Exists(trimmed) && !IsWindowsCommandName(trimmed))
        {
            return ApiResponse.Fail("path does not exist");
        }

#if WINDOWS
        if (OperatingSystem.IsWindows())
        {
            var registry = _sp.GetService<Nexus.Service.Helper.HelperRegistry>();
            var helperConnected = registry?.GetAny() is not null;
            if (helperConnected)
            {
                if (Directory.Exists(trimmed))
                {
                    if (await Nexus.Service.Helper.Domains.FileDialogCommands.OpenFolderAsync(registry!, trimmed).ConfigureAwait(false))
                    {
                        return ApiResponse.Ok("opened");
                    }
                    return ApiResponse.Fail("failed to open folder");
                }

                await Nexus.Service.Helper.Domains.SystemCommands.OpenFileAsync(registry!, trimmed).ConfigureAwait(false);
                return ApiResponse.Ok("opened");
            }

            if (!Environment.UserInteractive)
            {
                return ApiResponse.Fail("no interactive user session");
            }
        }
#endif
        await Task.CompletedTask.ConfigureAwait(false);
        try
        {
#if LINUX
            if (OperatingSystem.IsLinux())
            {
                // UseShellExecute's path handoff has no session-aware spawn point to
                // wrap, so a root daemon must invoke xdg-open explicitly instead.
                var (file, args) = Nexus.Service.Platform.Linux.LinuxSession.WrapSpawnAsSessionUser(
                    "xdg-open", new List<string> { trimmed });
                var psi = new ProcessStartInfo(file) { UseShellExecute = false };
                foreach (var a in args)
                    psi.ArgumentList.Add(a);
                Process.Start(psi);
            }
            else
#endif
            {
                Process.Start(new ProcessStartInfo(trimmed) { UseShellExecute = true });
            }
            return ApiResponse.Ok("opened");
        }
        catch (Exception ex)
        {
            return ApiResponse.Fail($"failed to open path: {ex.Message}");
        }
    }

    /// <summary>A bare command such as "calc" or "mspaint" (Elgato's Open action stores these): ShellExecute resolves it through the user's PATH and App Paths, which the service's own File.Exists cannot see.</summary>
    private static bool IsWindowsCommandName(string path) =>
        OperatingSystem.IsWindows() && path.IndexOfAny(['\\', '/', ':']) < 0 && path.Trim('.').Length > 0;

    public bool Lock() => _power.Lock();
    public bool Sleep() => _power.Sleep();
    public bool Shutdown() => _power.Shutdown();
    public bool Restart() => _power.Restart();
    public bool Logout() => _power.Logout();

    public bool SetDefaultOutput(string deviceId) => _audio.SetDefaultOutput(deviceId);
    public bool SetDefaultInput(string deviceId) => _audio.SetDefaultInput(deviceId);

    public VolumeState GetVolume() => _volume.GetState();
    public void SetVolume(double volume) => _volume.SetVolume(volume);
    public void SetMuted(bool muted) => _volume.SetMuted(muted);

    public bool LaunchShortcut(string targetId) => _shortcuts.Launch(targetId);

    /// <summary>
    /// Opens the OS task manager: Windows Task Manager, macOS Activity
    /// Monitor. Best effort; false when the platform has neither.
    /// </summary>
    public bool OpenTaskManager()
    {
        if (OperatingSystem.IsWindows())
        {
#if WINDOWS
            // Through the helper like every other launch, so its window comes to the front.
            var registry = _sp.GetService<Nexus.Service.Helper.HelperRegistry>();
            if (registry?.GetAny() is not null)
            {
                try
                {
                    Nexus.Service.Helper.Domains.SystemCommands.OpenFileAsync(registry, "taskmgr.exe").GetAwaiter().GetResult();
                    return true;
                }
                catch (Exception ex)
                {
                    Nexus.Service.Platform.ServiceLog.Warn($"[system-actions] task manager launch via helper failed: {ex.Message}");
                }
            }
            // The service runs as LocalSystem in Session 0, where a directly
            // spawned taskmgr.exe has no interactive desktop to draw on - run
            // it in the active console user's session instead, same mechanism
            // WindowsSystemPowerProvider.Lock() uses. A LocalSystem-privileged
            // taskmgr.exe launched with no user session (the pre-fix fallback)
            // spawns invisibly in Session 0 and leaks a privileged process per
            // press, so a missing session is a hard failure here, not a retry.
            if (Nexus.Service.Lifecycle.UserHelperBootstrapper.RunInUserSession("taskmgr.exe", "task-manager", "NexusTaskManager"))
            {
                return true;
            }
            Nexus.Service.Platform.ServiceLog.Warn("[system-actions] task manager launch via user session failed");
#endif
            return false;
        }
        if (OperatingSystem.IsMacOS())
        {
            return Nexus.Service.Platform.ShellExecutor.RunExit("open", 5000, "-a", "Activity Monitor") == 0;
        }
        if (OperatingSystem.IsLinux())
        {
#if LINUX
            // Try the GNOME and KDE system monitors in turn, then the generic
            // KDE ksysguard; no single tool covers every desktop environment.
            string[] candidates = ["gnome-system-monitor", "plasma-systemmonitor", "ksysguard"];
            foreach (var candidate in candidates)
            {
                // setpriv (the session-user wrapper below) exists even when the
                // wrapped candidate does not, so Process.Start would "succeed" on
                // a missing candidate - probe PATH first to fall through properly.
                if (!LinuxToolExists(candidate))
                {
                    continue;
                }
                try
                {
                    var (file, args) = Nexus.Service.Platform.Linux.LinuxSession.WrapSpawnAsSessionUser(
                        candidate, new List<string>());
                    var psi = new ProcessStartInfo(file) { UseShellExecute = false };
                    foreach (var a in args)
                        psi.ArgumentList.Add(a);
                    Process.Start(psi);
                    return true;
                }
                catch { /* launcher not installed - try the next */ }
            }
#endif
        }
        return false;
    }

#if LINUX
    private static bool LinuxToolExists(string tool) =>
        !string.IsNullOrWhiteSpace(Nexus.Service.Platform.ShellExecutor.Run("which", tool));
#endif

    /// <summary>
    /// Opens (or focuses) the Nexus dashboard window, the same mechanism
    /// POST /service/open-app uses: the service runs headless in Session 0
    /// (Windows), has no window of its own yet (macOS on first launch), or
    /// has no embedded browser host at all (Linux), so this delegates to the
    /// interactive-session launcher / app window owner / browser opener.
    /// </summary>
    public void OpenDashboard()
    {
#if WINDOWS
        if (OperatingSystem.IsWindows())
        {
            Nexus.Service.Lifecycle.UserHelperBootstrapper.LaunchOpenApp();
            return;
        }
#endif
#if MACOS
        if (OperatingSystem.IsMacOS())
        {
            // Focus only: a reload would race the editRequest frame a blank-key hold broadcasts first.
            Nexus.Service.Platform.Mac.MacAppWindow.OpenOrFocus(Nexus.Service.Platform.ServiceLaunchIntent.LocalDashboardUrl(0), navigateIfOpen: false);
        }
#endif
#if LINUX
        if (OperatingSystem.IsLinux())
        {
            Nexus.Service.Platform.Linux.LinuxBrowsers.OpenUrl(Nexus.Service.Platform.ServiceLaunchIntent.LocalDashboardUrl(0));
        }
#endif
    }

    /// <summary>
    /// Builds the inputter strokes for a key request. An explicit Strokes list
    /// wins; otherwise a single chord is expanded to a key-down then key-up
    /// (both carrying the modifier flags) so the combo presses and releases.
    /// </summary>
    private static InputterBody BuildKeyStrokes(SendKeysBody body)
    {
        if (body.Strokes is { Count: > 0 })
        {
            return new InputterBody { Strokes = body.Strokes };
        }
        if (string.IsNullOrEmpty(body.Key))
        {
            return new InputterBody();
        }
        return new InputterBody
        {
            Strokes =
            {
                new MacroStroke { Key = body.Key, Meta = body.Meta, Ctrl = body.Ctrl, Alt = body.Alt, Shift = body.Shift, Type = "keydown" },
                new MacroStroke { Key = body.Key, Meta = body.Meta, Ctrl = body.Ctrl, Alt = body.Alt, Shift = body.Shift, Type = "keyup" },
            },
        };
    }

    private static InputterBody PasteChord()
    {
        var mac = OperatingSystem.IsMacOS();
        MacroStroke V(string type) => new()
        {
            Key = "KeyV",
            Meta = mac,
            Ctrl = !mac,
            Type = type,
        };
        return new InputterBody { Strokes = { V("keydown"), V("keyup") } };
    }
}
