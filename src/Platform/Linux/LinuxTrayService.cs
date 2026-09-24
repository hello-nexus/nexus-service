using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Nexus.Service.Diagnostics;
using Nexus.Service.Panel;
using Nexus.Service.Platform.Linux.DBus;
using Nexus.Service.Sockets;
using Nexus.Service.Transfer;
using Microsoft.Extensions.Hosting;

namespace Nexus.Service.Platform.Linux;

/// <summary>
/// Spins up the KDE/SNI tray icon alongside the web service. Consumes the shared
/// <see cref="DBusConnection"/> singleton. Fail-soft: if D-Bus is unavailable
/// (headless / non-KDE), logs and keeps the web service running.
///
/// Windows-tray parity additions: surfaces a desktop notification when a phone
/// pairing request needs attention, and re-publishes the tray after the session
/// bus drops and reconnects (logout/relogin, bus restart) - the SNI registration
/// dies with the old connection otherwise.
/// </summary>
public sealed class LinuxTrayService : IHostedService
{
    private readonly IHostApplicationLifetime _lifetime;
    private readonly DBusConnection _dbus;
    private readonly PanelPhonePairingService _pairing;
    private readonly TransferInbox _transfer;
    private readonly DiagnosticsAlertService _diagAlerts;
    private readonly MultiplexHub _hub;
    private readonly ISystemAccentProvider _accent;
    private LinuxTrayHost? _host;
    // 1 while a start attempt is in flight or has produced a live host. Guards
    // the initial StartAsync against the session watcher firing mid-await, which
    // would otherwise build a second LinuxTrayHost and leak the first.
    private int _starting;
    private CancellationTokenSource? _accentCts;
    private string? _lastAccent;
    private string _url = "http://localhost:9400";

    public LinuxTrayService(IHostApplicationLifetime lifetime, DBusConnection dbus, PanelPhonePairingService pairing, TransferInbox transfer, DiagnosticsAlertService diagAlerts, MultiplexHub hub, ISystemAccentProvider accent)
    {
        _lifetime = lifetime;
        _dbus = dbus;
        _pairing = pairing;
        _transfer = transfer;
        _diagAlerts = diagAlerts;
        _hub = hub;
        _accent = accent;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        _url = ResolveUrl();
        _dbus.Reconnected += OnReconnected;
        // A root daemon that booted before anyone logged in has no session bus
        // yet, so this first attempt fails; the session watcher fires once the
        // user logs in and the tray registers then, without a service restart.
        LinuxSession.SessionAdopted += OnSessionAdopted;
        _pairing.PairRequestNeedsAttention += OnPairAttention;
        _transfer.TransferNeedsAttention += OnTransferAttention;
        _diagAlerts.AlertNeedsAttention += OnDiagnosticsAlert;
        await TryStartHostAsync(isRetry: false);
    }

    // Session adopted after startup: the bus that was missing exists now.
    private void OnSessionAdopted() => _ = TryStartHostAsync(isRetry: true);

    private async Task TryStartHostAsync(bool isRetry)
    {
        var failureLabel = isRetry ? "attach after login failed" : "disabled";
        if (Interlocked.CompareExchange(ref _starting, 1, 0) != 0)
            return;
        try
        {
            await _dbus.StartAsync();
            _host = new LinuxTrayHost(_dbus, _url, () => _lifetime.StopApplication());
            await _host.StartAsync();
            StartAccentWatch();
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[tray] {failureLabel}: {ex.Message}");
            _host?.Dispose();
            _host = null;
            Volatile.Write(ref _starting, 0);
            // The one-shot SessionAdopted can fire while this attempt is
            // unwinding, when its handler's CAS has already bounced off ours.
            if (!isRetry && LinuxSession.SessionUid is not null)
                await TryStartHostAsync(isRetry: true);
        }
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _dbus.Reconnected -= OnReconnected;
        LinuxSession.SessionAdopted -= OnSessionAdopted;
        _pairing.PairRequestNeedsAttention -= OnPairAttention;
        _transfer.TransferNeedsAttention -= OnTransferAttention;
        _diagAlerts.AlertNeedsAttention -= OnDiagnosticsAlert;
        _accentCts?.Cancel();
        _host?.Dispose();
        _host = null;
        return Task.CompletedTask;
    }

    // Session bus came back - the old SNI registration died with it, so rebuild
    // the tray host on the fresh connection. Fire-and-forget.
    private void OnReconnected() => _ = ReRegisterAsync();

    private async Task ReRegisterAsync()
    {
        try
        {
            _host?.Dispose();
            _host = new LinuxTrayHost(_dbus, _url, () => _lifetime.StopApplication());
            await _host.StartAsync();
            StartAccentWatch();
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[tray] re-register after reconnect failed: {ex.Message}");
        }
    }

    // Push the OS accent to the dashboard whenever it changes, so "system"
    // accent tracks the desktop live - the parity gap with light/dark, which
    // already follows the OS via the browser's prefers-color-scheme query. The
    // XDG portal emits SettingChanged on any appearance change; we re-read the
    // accent and broadcast only when it actually moved.
    private void StartAccentWatch()
    {
        _accentCts?.Cancel();
        var cts = new CancellationTokenSource();
        _accentCts = cts;
        _ = WatchAccentAsync(cts.Token);
    }

    private async Task WatchAccentAsync(CancellationToken ct)
    {
        try
        {
            await _dbus.AddMatchAsync("type='signal',interface='org.freedesktop.portal.Settings',member='SettingChanged'");
            _lastAccent = _accent.GetAccentHex();
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    // Long wait; the re-arm gap can drop a second change within
                    // the same instant, harmless for a user-driven accent flip.
                    await _dbus.WaitForSignalAsync("/org/freedesktop/portal/desktop", "SettingChanged", 600_000);
                }
                catch (TimeoutException)
                {
                    continue;
                }
                var hex = _accent.GetAccentHex();
                if (hex is not null && hex != _lastAccent)
                {
                    _lastAccent = hex;
                    PanelTopics.BroadcastSystemAccent(_hub, hex);
                }
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[accent] watch stopped: {ex.Message}");
        }
    }

    private void OnPairAttention(PanelPhonePairingService.PairAttentionNotice notice)
        => LinuxNotify.Send("Nexus pairing request",
            $"{(string.IsNullOrWhiteSpace(notice.DeviceLabel) ? "A device" : notice.DeviceLabel)} wants to pair.");

    // Transfer landed with no dashboard subscribed to the WS toast - the Linux
    // analog of the Windows tray balloon / macOS banner. notify-send has no
    // click action, so name the inbox folder in the body.
    private void OnTransferAttention(TransferAttentionNotice notice)
    {
        var body = notice.FolderPath is { Length: > 0 } folder
            ? $"{notice.Text} Saved to {AbbreviateHome(folder)}."
            : notice.Text;
        LinuxNotify.Send(notice.Title, body);
    }

    private void OnDiagnosticsAlert(DiagnosticsAlertNotice notice) =>
        Nexus.Service.Notifications.NotificationGate.SendOrHold(() => LinuxNotify.Send(notice.Title, notice.Text));

    private static string AbbreviateHome(string path)
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return !string.IsNullOrEmpty(home) && path.StartsWith(home + "/", StringComparison.Ordinal)
            ? "~" + path[home.Length..]
            : path;
    }

    private static string ResolveUrl()
    {
        var args = Environment.GetCommandLineArgs();
        for (int i = 1; i < args.Length; i++)
        {
            var a = args[i];
            if (a.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
                a.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            {
                return a.TrimEnd('/');
            }
        }
        return "http://localhost:9400";
    }
}
