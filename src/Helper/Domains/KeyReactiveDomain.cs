using System;
using System.Collections.Generic;
#if WINDOWS
using System.Runtime.Versioning;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Nexus.Service.Serialization;
#endif

namespace Nexus.Service.Helper.Domains;

/// <summary>Payload for <c>lighting.keyWatch</c>. Service-to-helper: arms or disarms the helper's key-press watcher.</summary>
public sealed class KeyWatchPayload
{
    public bool Enabled { get; set; }
}

/// <summary>Payload for <c>lighting.keyPressed</c>. Helper-to-service: canonical names of keys that just went down.</summary>
public sealed class KeyPressedPayload
{
    public List<string> Keys { get; set; } = new();
}

#if WINDOWS
/// <summary>Service-side facade for the helper's key-press watcher.</summary>
[SupportedOSPlatform("windows")]
public static class KeyReactiveCommands
{
    public const string WatchType = "lighting.keyWatch";
    public const string KeyPressedType = "lighting.keyPressed";

    public static Task SetKeyWatchAsync(HelperRegistry registry, bool enabled, CancellationToken ct = default)
    {
        var conn = registry.GetAny();
        if (conn is null) return Task.CompletedTask;
        return conn.SendAsync(
            type: WatchType,
            payload: new KeyWatchPayload { Enabled = enabled },
            payloadType: AppJsonContext.Default.KeyWatchPayload,
            ct: ct);
    }

    /// <summary>Keys named in a <c>lighting.keyPressed</c> envelope; empty when the payload is missing or malformed.</summary>
    public static IReadOnlyList<string> ParsePressed(HelperEnvelope env)
    {
        if (env.Payload is null) return Array.Empty<string>();
        try
        {
            return JsonSerializer.Deserialize(env.Payload.Value, AppJsonContext.Default.KeyPressedPayload)?.Keys
                ?? (IReadOnlyList<string>)Array.Empty<string>();
        }
        catch (JsonException)
        {
            return Array.Empty<string>();
        }
    }
}

/// <summary>Helper-side handler binding <c>lighting.keyWatch</c> to the watcher's arm switch.</summary>
[SupportedOSPlatform("windows")]
public sealed class KeyReactiveHandler
{
    private readonly Action<bool> _onWatchChanged;

    public KeyReactiveHandler(Action<bool> onWatchChanged) => _onWatchChanged = onWatchChanged;

    public void Register(HelperHandlerRegistry registry)
    {
        registry.Register(KeyReactiveCommands.WatchType, (env, _) =>
        {
            try
            {
                var enabled = env.Payload is not null
                    && JsonSerializer.Deserialize(env.Payload.Value, AppJsonContext.Default.KeyWatchPayload)?.Enabled == true;
                _onWatchChanged(enabled);
            }
            catch { }
            return Task.FromResult(env.Ok());
        });
    }
}
#endif
