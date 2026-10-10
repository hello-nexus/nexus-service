#if WINDOWS
using System;
using System.Collections.Generic;
using System.Runtime.Versioning;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Nexus.Service.Activity;
using Nexus.Service.Models.Activity;
using Nexus.Service.Serialization;

namespace Nexus.Service.Helper.Domains;

public sealed class ShortcutsRequest { public string TargetId { get; set; } = ""; }
public sealed class ShortcutListResult { public List<Shortcut> Shortcuts { get; set; } = new(); }
public sealed class ShortcutOneResult { public Shortcut? Shortcut { get; set; } }
public sealed class ShortcutIconResult { public byte[] Bytes { get; set; } = Array.Empty<byte>(); }
public sealed class ShortcutBoolResult { public bool Ok { get; set; } }
public sealed class ShortcutProcessNameResult { public string ProcessName { get; set; } = ""; }

/// <summary>
/// Service-side outbound facade for installed-app (Start menu) enumeration.
/// Get-StartApps is per-user and returns nothing for the Session-0 LocalSystem
/// service, so enumeration, icons, and launch all run in the user-session
/// helper - a direct Session-0 launch can't even resolve the target id.
/// </summary>
[SupportedOSPlatform("windows")]
public static class ShortcutsCommands
{
    public static async Task<IReadOnlyList<Shortcut>> GetAllAsync(HelperRegistry r, CancellationToken ct = default)
        => Read(await InvokeAsync(r, "shortcuts.getAll", new ShortcutsRequest(), ct), AppJsonContext.Default.ShortcutListResult)?.Shortcuts
           ?? new List<Shortcut>();

    public static async Task<Shortcut?> GetByIdAsync(HelperRegistry r, string id, CancellationToken ct = default)
        => Read(await InvokeAsync(r, "shortcuts.getById", new ShortcutsRequest { TargetId = id }, ct), AppJsonContext.Default.ShortcutOneResult)?.Shortcut;

    /// <summary>Null when the helper is not connected or the round trip failed; empty when it ran extraction and found no icon.</summary>
    public static async Task<byte[]?> GetIconAsync(HelperRegistry r, string id, CancellationToken ct = default)
    {
        var result = await InvokeAsync(r, "shortcuts.icon", new ShortcutsRequest { TargetId = id }, ct);
        if (result is null || !result.Ok)
        {
            return null;
        }
        return Read(result, AppJsonContext.Default.ShortcutIconResult)?.Bytes ?? Array.Empty<byte>();
    }

    public static async Task<bool> LaunchAsync(HelperRegistry r, string id, CancellationToken ct = default)
        => Read(await InvokeAsync(r, "shortcuts.launch", new ShortcutsRequest { TargetId = id }, ct), AppJsonContext.Default.ShortcutBoolResult)?.Ok ?? false;

    public static async Task<string> ResolveProcessNameAsync(HelperRegistry r, string id, CancellationToken ct = default)
        => Read(await InvokeAsync(r, "shortcuts.processName", new ShortcutsRequest { TargetId = id }, ct), AppJsonContext.Default.ShortcutProcessNameResult)?.ProcessName ?? "";

    // Enumeration shells out to Get-StartApps and then resolves every
    // shortcut's target, so this RPC has to outlast that whole pass or the
    // caller gets an empty app list.
    private const int EnumerateTimeoutMs = 25000;
    private const int DefaultTimeoutMs = 6000;

    private static async Task<HelperResult?> InvokeAsync(HelperRegistry r, string type, ShortcutsRequest payload, CancellationToken ct)
    {
        var conn = r.GetAny();
        if (conn is null) return null;
        var timeoutMs = type == "shortcuts.getAll" ? EnumerateTimeoutMs : DefaultTimeoutMs;
        return await conn.SendCommandAsync(type, payload, AppJsonContext.Default.ShortcutsRequest, timeoutMs: timeoutMs, ct: ct).ConfigureAwait(false);
    }

    private static T? Read<T>(HelperResult? r, System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> typeInfo)
    {
        if (r is null || !r.Ok || r.Payload is null) return default;
        try { return JsonSerializer.Deserialize(r.Payload.Value, typeInfo); }
        catch { return default; }
    }
}

[SupportedOSPlatform("windows")]
public sealed class ShortcutsHandler
{
    private readonly IShortcutsProvider _provider;
    public ShortcutsHandler(IShortcutsProvider provider) { _provider = provider; }

    public void Register(HelperHandlerRegistry registry)
    {
        registry.Register("shortcuts.getAll", (env, _) => Reply(env,
            new ShortcutListResult { Shortcuts = new(_provider.GetAll()) }, AppJsonContext.Default.ShortcutListResult));
        registry.Register("shortcuts.getById", (env, _) => Reply(env,
            new ShortcutOneResult { Shortcut = _provider.GetById(ReadReq(env).TargetId) }, AppJsonContext.Default.ShortcutOneResult));
        registry.Register("shortcuts.icon", (env, _) => Reply(env,
            new ShortcutIconResult { Bytes = _provider.GetIcon(ReadReq(env).TargetId) ?? Array.Empty<byte>() }, AppJsonContext.Default.ShortcutIconResult));
        registry.Register("shortcuts.launch", (env, _) => Reply(env,
            new ShortcutBoolResult { Ok = _provider.Launch(ReadReq(env).TargetId) }, AppJsonContext.Default.ShortcutBoolResult));
        registry.Register("shortcuts.processName", (env, _) => Reply(env,
            new ShortcutProcessNameResult { ProcessName = _provider.ResolveProcessName(ReadReq(env).TargetId) },
            AppJsonContext.Default.ShortcutProcessNameResult));
    }

    private static ShortcutsRequest ReadReq(HelperEnvelope env)
        => env.Payload is null
            ? new ShortcutsRequest()
            : JsonSerializer.Deserialize(env.Payload.Value, AppJsonContext.Default.ShortcutsRequest) ?? new ShortcutsRequest();

    private static Task<HelperResult> Reply<T>(HelperEnvelope env, T value, System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> typeInfo)
        => Task.FromResult(new HelperResult
        {
            Id = env.Id ?? "",
            Ok = true,
            Payload = JsonSerializer.SerializeToElement(value, typeInfo),
        });
}
#endif
