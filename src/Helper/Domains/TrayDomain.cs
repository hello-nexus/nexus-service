#if WINDOWS
using System;
using System.Runtime.Versioning;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Nexus.Service.Serialization;

namespace Nexus.Service.Helper.Domains;

/// <summary>
/// Payload for <c>trayIcon.setVisible</c>. Service-to-helper. The
/// service pushes the persisted <c>ShowWindowsTrayIcon</c> setting on
/// every connect so the helper settles to the configured value within
/// ~1 s of bootstrap.
/// </summary>
public sealed class TraySetVisiblePayload
{
    public bool Visible { get; set; }
}

/// <summary>
/// Payload for <c>trayIcon.pairNotice</c>. Service-to-helper. The service
/// raises this when a phone submits a pair request and no dashboard is
/// open to surface the Allow/Deny modal. <see cref="Show"/> true pops a
/// tray balloon (click → opens the dashboard); false dismisses it once
/// the request is resolved.
/// </summary>
public sealed class TrayPairNoticePayload
{
    public bool Show { get; set; }
    public string DeviceLabel { get; set; } = "";
}

/// <summary>
/// Payload for <c>trayIcon.notice</c>. Service-to-helper. Generic one-shot
/// balloon (first user: incoming phone→PC transfers landing while no
/// dashboard is open). When <see cref="FolderPath"/> is set, clicking the
/// balloon opens that folder in Explorer instead of the dashboard.
/// </summary>
public sealed class TrayNoticePayload
{
    public string Title { get; set; } = "";
    public string Text { get; set; } = "";
    public string FolderPath { get; set; } = "";
    /// <summary>SPA path a click opens, when the balloon has no folder; empty lands on the dashboard.</summary>
    public string WindowPath { get; set; } = "";
    /// <summary>Label for an action button on the notice. Non-empty asks for an interactive toast, which the helper falls back off to the plain balloon when unavailable.</summary>
    public string ButtonLabel { get; set; } = "";
}

/// <summary>Payload for <c>trayIcon.updateReady</c>. Service-to-helper.</summary>
public sealed class TrayUpdateReadyPayload
{
    public string Version { get; set; } = "";
}

/// <summary>Payload for <c>trayIcon.openDashboard</c>. Service-to-helper. No fields required.</summary>
public sealed class TrayOpenDashboardPayload
{
}

/// <summary>Payload for <c>trayIcon.showUpdaterWindow</c>. Service-to-helper.</summary>
public sealed class TrayShowUpdaterWindowPayload
{
    public string FromVersion { get; set; } = "";
    public string ToVersion { get; set; } = "";
}

/// <summary>Payload for <c>trayIcon.closeUpdaterWindow</c>. Service-to-helper. No fields required.</summary>
public sealed class TrayCloseUpdaterWindowPayload
{
}

// JSON source-gen registration lives in src/Serialization/AppJsonContext.cs
// (see note in LifecycleDomain.cs).

[SupportedOSPlatform("windows")]
public static class TrayCommands
{
    public static Task SetVisibleAsync(HelperRegistry registry, bool visible, CancellationToken ct = default)
    {
        var conn = registry.GetAny();
        if (conn is null) return Task.CompletedTask;
        return conn.SendAsync(
            type: "trayIcon.setVisible",
            payload: new TraySetVisiblePayload { Visible = visible },
            payloadType: AppJsonContext.Default.TraySetVisiblePayload,
            ct: ct);
    }

    public static Task PairNoticeAsync(HelperRegistry registry, bool show, string deviceLabel, CancellationToken ct = default)
    {
        // Held while first-run onboarding owns the screen; released after.
        if (Nexus.Service.Notifications.NotificationGate.TryHold(
                () => SendAsync_pairNotice(registry, show, deviceLabel, ct), out var gated))
        {
            return gated;
        }
        return SendAsync_pairNotice(registry, show, deviceLabel, ct);
    }

    private static Task SendAsync_pairNotice(HelperRegistry registry, bool show, string deviceLabel, CancellationToken ct)
    {
        var conn = registry.GetAny();
        if (conn is null) return Task.CompletedTask;
        return conn.SendAsync(
            type: "trayIcon.pairNotice",
            payload: new TrayPairNoticePayload { Show = show, DeviceLabel = deviceLabel ?? "" },
            payloadType: AppJsonContext.Default.TrayPairNoticePayload,
            ct: ct);
    }

    public static Task NoticeAsync(HelperRegistry registry, string title, string text, string? folderPath, string? windowPath = null, string? buttonLabel = null, CancellationToken ct = default)
    {
        // Held while first-run onboarding owns the screen; released after.
        if (Nexus.Service.Notifications.NotificationGate.TryHold(
                () => SendAsync_notice(registry, title, text, folderPath, windowPath, buttonLabel, ct), out var gated))
        {
            return gated;
        }
        return SendAsync_notice(registry, title, text, folderPath, windowPath, buttonLabel, ct);
    }

    private static Task SendAsync_notice(HelperRegistry registry, string title, string text, string? folderPath, string? windowPath, string? buttonLabel, CancellationToken ct)
    {
        var conn = registry.GetAny();
        if (conn is null) return Task.CompletedTask;
        return conn.SendAsync(
            type: "trayIcon.notice",
            payload: new TrayNoticePayload
            {
                Title = title,
                Text = text,
                FolderPath = folderPath ?? "",
                WindowPath = windowPath ?? "",
                ButtonLabel = buttonLabel ?? "",
            },
            payloadType: AppJsonContext.Default.TrayNoticePayload,
            ct: ct);
    }

    public static Task UpdateReadyAsync(HelperRegistry registry, string version, CancellationToken ct = default)
    {
        // Held while first-run onboarding owns the screen; released after.
        if (Nexus.Service.Notifications.NotificationGate.TryHold(
                () => SendAsync_updateReady(registry, version, ct), out var gated))
        {
            return gated;
        }
        return SendAsync_updateReady(registry, version, ct);
    }

    private static Task SendAsync_updateReady(HelperRegistry registry, string version, CancellationToken ct)
    {
        var conn = registry.GetAny();
        if (conn is null) return Task.CompletedTask;
        return conn.SendAsync(
            type: "trayIcon.updateReady",
            payload: new TrayUpdateReadyPayload { Version = version },
            payloadType: AppJsonContext.Default.TrayUpdateReadyPayload,
            ct: ct);
    }

    public static Task OpenDashboardAsync(HelperRegistry registry, CancellationToken ct = default)
    {
        var conn = registry.GetAny();
        if (conn is null) return Task.CompletedTask;
        return conn.SendAsync(
            type: "trayIcon.openDashboard",
            payload: new TrayOpenDashboardPayload(),
            payloadType: AppJsonContext.Default.TrayOpenDashboardPayload,
            ct: ct);
    }

    public static Task ShowUpdaterWindowAsync(HelperRegistry registry, string fromVersion, string toVersion, CancellationToken ct = default)
    {
        var conn = registry.GetAny();
        if (conn is null) return Task.CompletedTask;
        return conn.SendAsync(
            type: "trayIcon.showUpdaterWindow",
            payload: new TrayShowUpdaterWindowPayload { FromVersion = fromVersion, ToVersion = toVersion },
            payloadType: AppJsonContext.Default.TrayShowUpdaterWindowPayload,
            ct: ct);
    }

    public static Task CloseUpdaterWindowAsync(HelperRegistry registry, CancellationToken ct = default)
    {
        var conn = registry.GetAny();
        return conn is null ? Task.CompletedTask : CloseUpdaterWindowAsync(conn, ct);
    }

    public static Task CloseUpdaterWindowAsync(HelperConnection conn, CancellationToken ct = default)
    {
        return conn.SendAsync(
            type: "trayIcon.closeUpdaterWindow",
            payload: new TrayCloseUpdaterWindowPayload(),
            payloadType: AppJsonContext.Default.TrayCloseUpdaterWindowPayload,
            ct: ct);
    }
}

[SupportedOSPlatform("windows")]
public sealed class TrayHandler
{
    private readonly Action<bool> _setVisible;
    private readonly Action<string> _showPairNotice;
    private readonly Action _dismissPairNotice;
    private readonly Action<string, string, string?, string?, string?> _showNotice;
    private readonly Action<string> _showUpdateReady;
    private readonly Action _openDashboard;
    private readonly Action<string, string> _showUpdaterWindow;
    private readonly Action _closeUpdaterWindow;
    // Deduplicates balloon: skip if the version was already notified.
    private string? _notifiedVersion;

    public TrayHandler(
        Action<bool> setVisible,
        Action<string> showPairNotice,
        Action dismissPairNotice,
        Action<string, string, string?, string?, string?> showNotice,
        Action<string> showUpdateReady,
        Action openDashboard,
        Action<string, string> showUpdaterWindow,
        Action closeUpdaterWindow)
    {
        _setVisible = setVisible;
        _showPairNotice = showPairNotice;
        _dismissPairNotice = dismissPairNotice;
        _showNotice = showNotice;
        _showUpdateReady = showUpdateReady;
        _openDashboard = openDashboard;
        _showUpdaterWindow = showUpdaterWindow;
        _closeUpdaterWindow = closeUpdaterWindow;
    }

    public void Register(HelperHandlerRegistry registry)
    {
        registry.Register("trayIcon.setVisible", (env, _) =>
        {
            if (env.Payload is null) return Task.FromResult(env.Ok());
            var p = JsonSerializer.Deserialize(env.Payload.Value, AppJsonContext.Default.TraySetVisiblePayload);
            _setVisible(p?.Visible ?? false);
            return Task.FromResult(env.Ok());
        });

        registry.Register("trayIcon.pairNotice", (env, _) =>
        {
            if (env.Payload is null) return Task.FromResult(env.Ok());
            var p = JsonSerializer.Deserialize(env.Payload.Value, AppJsonContext.Default.TrayPairNoticePayload);
            if (p is not null)
            {
                if (p.Show) _showPairNotice(p.DeviceLabel ?? "");
                else _dismissPairNotice();
            }
            return Task.FromResult(env.Ok());
        });

        registry.Register("trayIcon.notice", (env, _) =>
        {
            if (env.Payload is null) return Task.FromResult(env.Ok());
            var p = JsonSerializer.Deserialize(env.Payload.Value, AppJsonContext.Default.TrayNoticePayload);
            if (p is not null)
            {
                _showNotice(
                    p.Title ?? "",
                    p.Text ?? "",
                    string.IsNullOrEmpty(p.FolderPath) ? null : p.FolderPath,
                    string.IsNullOrEmpty(p.WindowPath) ? null : p.WindowPath,
                    string.IsNullOrEmpty(p.ButtonLabel) ? null : p.ButtonLabel);
            }
            return Task.FromResult(env.Ok());
        });

        registry.Register("trayIcon.updateReady", (env, _) =>
        {
            if (env.Payload is null) return Task.FromResult(env.Ok());
            var p = JsonSerializer.Deserialize(env.Payload.Value, AppJsonContext.Default.TrayUpdateReadyPayload);
            if (p is not null && !string.IsNullOrEmpty(p.Version) && p.Version != _notifiedVersion)
            {
                _notifiedVersion = p.Version;
                _showUpdateReady(p.Version);
            }
            return Task.FromResult(env.Ok());
        });

        registry.Register("trayIcon.openDashboard", (env, _) =>
        {
            _openDashboard();
            return Task.FromResult(env.Ok());
        });

        registry.Register("trayIcon.showUpdaterWindow", (env, _) =>
        {
            if (env.Payload is null) return Task.FromResult(env.Ok());
            var p = JsonSerializer.Deserialize(env.Payload.Value, AppJsonContext.Default.TrayShowUpdaterWindowPayload);
            if (p is not null)
            {
                _showUpdaterWindow(p.FromVersion ?? "", p.ToVersion ?? "");
            }
            return Task.FromResult(env.Ok());
        });

        registry.Register("trayIcon.closeUpdaterWindow", (env, _) =>
        {
            _closeUpdaterWindow();
            return Task.FromResult(env.Ok());
        });
    }
}
#endif
