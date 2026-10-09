#if WINDOWS
using System;
using System.Runtime.Versioning;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Nexus.Service.Platform.Windows;
using Nexus.Service.Serialization;

namespace Nexus.Service.Helper.Domains;

public sealed class OpenSettingsPayload { }

public sealed class OpenUrlPayload
{
    public string Url { get; set; } = "";
}

public sealed class OpenFilePayload
{
    public string Path { get; set; } = "";
}

/// <summary>Helper-to-service push: a window the helper could not focus because it runs elevated.</summary>
public sealed class FocusWindowPayload
{
    public long Hwnd { get; set; }
    public long ExpectedForeground { get; set; }
}

/// <summary>Helper-to-service push: the Windows apps light/dark setting.</summary>
public sealed class SystemThemePayload
{
    public bool Dark { get; set; }
}

[SupportedOSPlatform("windows")]
public static class SystemCommands
{
    public const string OpenSettingsType = "system.openSettings";
    public const string OpenUrlType = "system.openUrl";
    public const string OpenFileType = "system.openFile";
    public const string ThemeChangedType = "system.themeChanged";
    public const string FocusWindowElevatedType = "system.focusWindowElevated";

    public static Task OpenSettingsAsync(HelperRegistry registry, CancellationToken ct = default)
    {
        var conn = registry.GetAny();
        if (conn is null)
        {
            return Task.CompletedTask;
        }

        return conn.SendAsync(
            type: OpenSettingsType,
            payload: new OpenSettingsPayload(),
            payloadType: AppJsonContext.Default.OpenSettingsPayload,
            ct: ct);
    }

    public static Task OpenUrlAsync(HelperRegistry registry, string url, CancellationToken ct = default)
    {
        var conn = registry.GetAny();
        if (conn is null)
        {
            return Task.CompletedTask;
        }

        return conn.SendAsync(
            type: OpenUrlType,
            payload: new OpenUrlPayload { Url = url },
            payloadType: AppJsonContext.Default.OpenUrlPayload,
            ct: ct);
    }

    public static Task OpenFileAsync(HelperRegistry registry, string path, CancellationToken ct = default)
    {
        var conn = registry.GetAny();
        if (conn is null)
        {
            return Task.CompletedTask;
        }

        return conn.SendAsync(
            type: OpenFileType,
            payload: new OpenFilePayload { Path = path },
            payloadType: AppJsonContext.Default.OpenFilePayload,
            ct: ct);
    }
}

[SupportedOSPlatform("windows")]
public sealed class SystemHandler
{
    public void Register(HelperHandlerRegistry registry)
    {
        registry.Register(SystemCommands.OpenSettingsType, (env, _) =>
        {
            try
            {
                ForegroundNudge.OpenSettingsOverApp();
            }
            catch { }
            return Task.FromResult(env.Ok());
        });

        registry.Register(SystemCommands.OpenUrlType, (env, _) =>
        {
            try
            {
                var payload = env.Payload is null
                    ? new OpenUrlPayload()
                    : JsonSerializer.Deserialize(env.Payload.Value, AppJsonContext.Default.OpenUrlPayload) ?? new OpenUrlPayload();
                if (!string.IsNullOrWhiteSpace(payload.Url))
                {
                    ForegroundNudge.OpenUrlOverApp(payload.Url);
                }
            }
            catch { }
            return Task.FromResult(env.Ok());
        });

        registry.Register(SystemCommands.OpenFileType, (env, _) =>
        {
            try
            {
                var payload = env.Payload is null
                    ? new OpenFilePayload()
                    : JsonSerializer.Deserialize(env.Payload.Value, AppJsonContext.Default.OpenFilePayload) ?? new OpenFilePayload();
                if (!string.IsNullOrWhiteSpace(payload.Path))
                {
                    ForegroundNudge.OpenFileOverApp(payload.Path);
                }
            }
            catch { }
            return Task.FromResult(env.Ok());
        });
    }
}
#endif
