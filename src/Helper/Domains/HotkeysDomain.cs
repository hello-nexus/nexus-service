using System;
using System.Collections.Generic;
#if WINDOWS
using System.Runtime.Versioning;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Nexus.Service.Hotkeys;
using Nexus.Service.Serialization;
#endif

namespace Nexus.Service.Helper.Domains;

/// <summary>One combo for <c>hotkeys.set</c>: the binding id and its canonical combo text.</summary>
public sealed class HotkeySetItem
{
    public string Id { get; set; } = "";
    public string Combo { get; set; } = "";
}

/// <summary>Payload for <c>hotkeys.set</c>. Service-to-helper: the complete set of combos to register; anything registered before and absent here is unregistered.</summary>
public sealed class HotkeysSetPayload
{
    public List<HotkeySetItem> Bindings { get; set; } = new();
}

/// <summary>One id's outcome in the <c>hotkeys.set</c> reply.</summary>
public sealed class HotkeySetOutcome
{
    public string Id { get; set; } = "";
    public bool Ok { get; set; }
}

/// <summary>Reply payload of <c>hotkeys.set</c>: per-id registration success.</summary>
public sealed class HotkeysSetResult
{
    public List<HotkeySetOutcome> Results { get; set; } = new();
}

/// <summary>Payload for <c>hotkeys.fired</c>. Helper-to-service: a registered combo was pressed.</summary>
public sealed class HotkeyFiredPayload
{
    public string Id { get; set; } = "";
    /// <summary>Canonical combo the helper registered for <see cref="Id"/>; the service ignores a press whose combo is no longer the binding's.</summary>
    public string Combo { get; set; } = "";
}

/// <summary>Payload for <c>hotkeys.toggleWindow</c>. Service-to-helper: show the Nexus window, or hide it when it is frontmost.</summary>
public sealed class HotkeyToggleWindowPayload
{
}

#if WINDOWS
/// <summary>Service-side facade for the helper's global hotkey registration.</summary>
[SupportedOSPlatform("windows")]
public static class HotkeyCommands
{
    public const string SetType = "hotkeys.set";
    public const string FiredType = "hotkeys.fired";
    public const string ToggleWindowType = "hotkeys.toggleWindow";

    private const int SetTimeoutMs = 5000;

    /// <summary>Sends the full combo set to <paramref name="conn"/> and returns per-id success; null when the round trip failed.</summary>
    public static async Task<IReadOnlyDictionary<string, bool>?> SetAsync(
        HelperConnection conn, IReadOnlyList<HotkeyRegistration> set, CancellationToken ct = default)
    {
        var payload = new HotkeysSetPayload();
        foreach (var reg in set)
        {
            payload.Bindings.Add(new HotkeySetItem { Id = reg.Id, Combo = reg.Combo.ToString() });
        }
        var result = await conn.SendCommandAsync(SetType, payload, AppJsonContext.Default.HotkeysSetPayload, SetTimeoutMs, ct).ConfigureAwait(false);
        if (!result.Ok || result.Payload is null)
        {
            return null;
        }
        try
        {
            var parsed = JsonSerializer.Deserialize(result.Payload.Value, AppJsonContext.Default.HotkeysSetResult);
            var map = new Dictionary<string, bool>(StringComparer.Ordinal);
            foreach (var outcome in parsed?.Results ?? new List<HotkeySetOutcome>())
            {
                map[outcome.Id] = outcome.Ok;
            }
            return map;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public static Task ToggleWindowAsync(HelperRegistry registry, CancellationToken ct = default)
    {
        var conn = registry.GetAny();
        if (conn is null)
        {
            return Task.CompletedTask;
        }
        return conn.SendAsync(
            type: ToggleWindowType,
            payload: new HotkeyToggleWindowPayload(),
            payloadType: AppJsonContext.Default.HotkeyToggleWindowPayload,
            ct: ct);
    }

    /// <summary>The binding id and combo named in a <c>hotkeys.fired</c> envelope; null when missing or malformed.</summary>
    public static (string Id, string Combo)? ParseFired(HelperEnvelope env)
    {
        if (env.Payload is null)
        {
            return null;
        }
        try
        {
            var parsed = JsonSerializer.Deserialize(env.Payload.Value, AppJsonContext.Default.HotkeyFiredPayload);
            return parsed is null || string.IsNullOrEmpty(parsed.Id) ? null : (parsed.Id, parsed.Combo);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}

/// <summary>Helper-side handlers: register the pushed combos, and toggle the window on request.</summary>
[SupportedOSPlatform("windows")]
public sealed class HotkeysHandler
{
    private readonly Func<IReadOnlyList<HotkeyRegistration>, Task<IReadOnlyDictionary<string, bool>>> _apply;
    private readonly Action _toggleWindow;

    public HotkeysHandler(
        Func<IReadOnlyList<HotkeyRegistration>, Task<IReadOnlyDictionary<string, bool>>> apply,
        Action toggleWindow)
    {
        _apply = apply;
        _toggleWindow = toggleWindow;
    }

    public void Register(HelperHandlerRegistry registry)
    {
        registry.Register(HotkeyCommands.SetType, async (env, _) =>
        {
            var payload = env.Payload is null
                ? new HotkeysSetPayload()
                : JsonSerializer.Deserialize(env.Payload.Value, AppJsonContext.Default.HotkeysSetPayload) ?? new HotkeysSetPayload();
            var set = new List<HotkeyRegistration>();
            var result = new HotkeysSetResult();
            foreach (var item in payload.Bindings)
            {
                if (HotkeyCombo.TryParse(item.Combo) is { } combo)
                {
                    set.Add(new HotkeyRegistration(item.Id, combo));
                }
                else
                {
                    result.Results.Add(new HotkeySetOutcome { Id = item.Id, Ok = false });
                }
            }
            var applied = await _apply(set).ConfigureAwait(false);
            foreach (var reg in set)
            {
                result.Results.Add(new HotkeySetOutcome { Id = reg.Id, Ok = applied.TryGetValue(reg.Id, out var ok) && ok });
            }
            return new HelperResult
            {
                Id = env.Id ?? "",
                Ok = true,
                Payload = JsonSerializer.SerializeToElement(result, AppJsonContext.Default.HotkeysSetResult),
            };
        });

        registry.Register(HotkeyCommands.ToggleWindowType, (env, _) =>
        {
            try
            {
                _toggleWindow();
            }
            catch
            {
                // Best-effort UI action; the next press retries.
            }
            return Task.FromResult(env.Ok());
        });
    }
}
#endif
