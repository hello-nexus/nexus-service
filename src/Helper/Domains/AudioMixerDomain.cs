#if WINDOWS
using System;
using System.Collections.Generic;
using System.Runtime.Versioning;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Nexus.Service.Models.Activity;
using Nexus.Service.Serialization;

namespace Nexus.Service.Helper.Domains;

/// <summary>
/// Payload for <c>audioMixer.snapshot</c>. Helper-to-service push, sent only
/// when the strip set or any level/peak actually changed.
/// </summary>
public sealed class AudioMixerSnapshotPayload
{
    public List<AudioSessionDto> Sessions { get; set; } = new();
}

/// <summary>
/// Payload for <c>audioMixer.stream</c>. Service-to-helper. Enabled raises the
/// helper's sampling rate to something a level meter can animate; disabled
/// drops back to the idle rate that only tracks which apps exist.
/// </summary>
public sealed class AudioMixerStreamPayload
{
    public bool Enabled { get; set; }
}

/// <summary>Payload for <c>audioDevice.setDefault</c>. Service-to-helper.</summary>
public sealed class AudioDeviceDefaultPayload
{
    public string DeviceId { get; set; } = "";
}

/// <summary>Reply for <c>audioDevice.setDefault</c>: whether the endpoint moved.</summary>
public sealed class AudioDeviceDefaultResult
{
    public bool Ok { get; set; }
}

/// <summary>
/// Payload for <c>audioMixer.set</c>. Service-to-helper. The Has* flags carry
/// "leave this alone" across the wire, which a bare double/bool cannot.
/// </summary>
public sealed class AudioMixerSetPayload
{
    public string Id { get; set; } = "";
    public bool HasVolume { get; set; }
    public double Volume { get; set; }
    public bool HasMuted { get; set; }
    public bool Muted { get; set; }
    /// <summary>Touch only sessions on the default output endpoint. Absent from an
    /// older service, which means every endpoint, as before.</summary>
    public bool DefaultOnly { get; set; }
}

// JSON source-gen registration lives in src/Serialization/AppJsonContext.cs.

[SupportedOSPlatform("windows")]
public static class AudioMixerCommands
{
    public const string SnapshotType = "audioMixer.snapshot";

    public static Task StreamAsync(HelperRegistry registry, bool enabled, CancellationToken ct = default)
    {
        var conn = registry.GetAny();
        if (conn is null) return Task.CompletedTask;
        return conn.SendAsync(
            type: "audioMixer.stream",
            payload: new AudioMixerStreamPayload { Enabled = enabled },
            payloadType: AppJsonContext.Default.AudioMixerStreamPayload,
            ct: ct);
    }

    /// <summary>
    /// The default endpoint is per-user, so the switch has to run as the console
    /// user. Returns false with no helper connected.
    /// </summary>
    public static async Task<bool> SetDefaultDeviceAsync(HelperRegistry registry, string deviceId, CancellationToken ct = default)
    {
        var conn = registry.GetAny();
        if (conn is null) return false;
        var res = await conn.SendCommandAsync(
            "audioDevice.setDefault",
            new AudioDeviceDefaultPayload { DeviceId = deviceId },
            AppJsonContext.Default.AudioDeviceDefaultPayload,
            timeoutMs: 5000,
            ct: ct).ConfigureAwait(false);
        if (res is null || !res.Ok || res.Payload is null) return false;
        try { return JsonSerializer.Deserialize(res.Payload.Value, AppJsonContext.Default.AudioDeviceDefaultResult)?.Ok ?? false; }
        catch { return false; }
    }

    public static Task SetAsync(HelperRegistry registry, string id, double? volume, bool? muted, bool defaultOnly = false, CancellationToken ct = default)
    {
        var conn = registry.GetAny();
        if (conn is null) return Task.CompletedTask;
        return conn.SendAsync(
            type: "audioMixer.set",
            payload: new AudioMixerSetPayload
            {
                Id = id,
                HasVolume = volume.HasValue,
                Volume = volume ?? 0,
                HasMuted = muted.HasValue,
                Muted = muted ?? false,
                DefaultOnly = defaultOnly,
            },
            payloadType: AppJsonContext.Default.AudioMixerSetPayload,
            ct: ct);
    }
}

[SupportedOSPlatform("windows")]
public sealed class AudioMixerHandler
{
    private readonly Action<bool> _setStreaming;
    private readonly Action<string, double?, bool?, bool> _apply;
    private readonly Func<string, bool> _setDefaultDevice;

    public AudioMixerHandler(
        Action<bool> setStreaming,
        Action<string, double?, bool?, bool> apply,
        Func<string, bool> setDefaultDevice)
    {
        _setStreaming = setStreaming;
        _apply = apply;
        _setDefaultDevice = setDefaultDevice;
    }

    public void Register(HelperHandlerRegistry registry)
    {
        registry.Register("audioMixer.stream", (env, _) =>
        {
            if (env.Payload is null) return Task.FromResult(env.Ok());
            var p = JsonSerializer.Deserialize(env.Payload.Value, AppJsonContext.Default.AudioMixerStreamPayload);
            if (p is not null)
            {
                try { _setStreaming(p.Enabled); } catch { }
            }
            return Task.FromResult(env.Ok());
        });

        registry.Register("audioMixer.set", (env, _) =>
        {
            if (env.Payload is null) return Task.FromResult(env.Ok());
            var p = JsonSerializer.Deserialize(env.Payload.Value, AppJsonContext.Default.AudioMixerSetPayload);
            if (p is not null && p.Id.Length > 0)
            {
                try { _apply(p.Id, p.HasVolume ? p.Volume : null, p.HasMuted ? p.Muted : null, p.DefaultOnly); } catch { }
            }
            return Task.FromResult(env.Ok());
        });

        registry.Register("audioDevice.setDefault", (env, _) =>
        {
            var ok = false;
            if (env.Payload is not null)
            {
                var p = JsonSerializer.Deserialize(env.Payload.Value, AppJsonContext.Default.AudioDeviceDefaultPayload);
                if (p is not null && p.DeviceId.Length > 0)
                {
                    try { ok = _setDefaultDevice(p.DeviceId); } catch { }
                }
            }
            return Task.FromResult(new HelperResult
            {
                Id = env.Id ?? "",
                Ok = true,
                Payload = JsonSerializer.SerializeToElement(
                    new AudioDeviceDefaultResult { Ok = ok }, AppJsonContext.Default.AudioDeviceDefaultResult),
            });
        });
    }
}
#endif
