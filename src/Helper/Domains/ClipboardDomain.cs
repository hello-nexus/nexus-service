#if WINDOWS
using System;
using System.Runtime.Versioning;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Nexus.Service.Serialization;

namespace Nexus.Service.Helper.Domains;

/// <summary>
/// Payload for <c>clipboard.setText</c> and <c>clipboard.setTextAndPaste</c>.
/// Service-to-helper. Clipboards are per-session: a set from the Session-0
/// service lands on an invisible clipboard, so the user-session helper
/// applies it instead.
/// </summary>
public sealed class ClipboardSetTextPayload
{
    public string Text { get; set; } = "";
}

// JSON source-gen registration lives in src/Serialization/AppJsonContext.cs
// (see note in LifecycleDomain.cs).

[SupportedOSPlatform("windows")]
public static class ClipboardCommands
{
    /// <summary>True when a user-session helper applied the text.</summary>
    public static async Task<bool> SetTextAsync(HelperRegistry registry, string text, CancellationToken ct = default)
    {
        var conn = registry.GetAny();
        if (conn is null)
        {
            return false;
        }
        var result = await conn.SendCommandAsync(
            "clipboard.setText",
            new ClipboardSetTextPayload { Text = text ?? "" },
            AppJsonContext.Default.ClipboardSetTextPayload,
            timeoutMs: 5000,
            ct: ct).ConfigureAwait(false);
        if (!result.Ok)
        {
            // Distinguishes pipe skew ("unknown type") from timeout from a
            // helper-side Set-Clipboard failure.
            Nexus.Service.Platform.ServiceLog.Warn($"[clipboard-win] helper set failed: {result.Error}");
        }
        return result.Ok;
    }

    /// <summary>
    /// True when a user-session helper set the clipboard and injected the
    /// paste chord (Ctrl+V). Used by the deck/panel "text" action: a plain
    /// clipboard.setText from the service would still leave the paste
    /// keystroke stuck on SendInput's Session-0 desktop, so the helper does
    /// both steps itself, in its own session, in one round trip.
    /// </summary>
    public static async Task<bool> SetTextAndPasteAsync(HelperRegistry registry, string text, CancellationToken ct = default)
    {
        var conn = registry.GetAny();
        if (conn is null)
        {
            return false;
        }
        var result = await conn.SendCommandAsync(
            "clipboard.setTextAndPaste",
            new ClipboardSetTextPayload { Text = text ?? "" },
            AppJsonContext.Default.ClipboardSetTextPayload,
            timeoutMs: 5000,
            ct: ct).ConfigureAwait(false);
        if (!result.Ok)
        {
            Nexus.Service.Platform.ServiceLog.Warn($"[clipboard-win] helper paste failed: {result.Error}");
        }
        return result.Ok;
    }
}

[SupportedOSPlatform("windows")]
public sealed class ClipboardHandler
{
    private readonly Func<string, bool> _setText;

    public ClipboardHandler(Func<string, bool> setText) => _setText = setText;

    public void Register(HelperHandlerRegistry registry)
    {
        registry.Register("clipboard.setText", (env, _) =>
        {
            var ok = false;
            if (env.Payload is not null)
            {
                var p = JsonSerializer.Deserialize(env.Payload.Value, AppJsonContext.Default.ClipboardSetTextPayload);
                ok = p is not null && _setText(p.Text ?? "");
            }
            if (!ok)
            {
                Nexus.Service.Platform.ServiceLog.Warn("[clipboard-helper] clipboard.setText: Set-Clipboard did not commit (see the preceding [clipboard-win] line for the exit code/stderr)");
            }
            return Task.FromResult(ok ? env.Ok() : HelperResult.Fail(env.Id, "clipboard set failed"));
        });

        registry.Register("clipboard.setTextAndPaste", (env, _) =>
        {
            if (env.Payload is null)
            {
                return Task.FromResult(HelperResult.Fail(env.Id, "paste failed: no payload"));
            }
            var p = JsonSerializer.Deserialize(env.Payload.Value, AppJsonContext.Default.ClipboardSetTextPayload);
            if (p is null)
            {
                return Task.FromResult(HelperResult.Fail(env.Id, "paste failed: bad payload"));
            }
            if (!_setText(p.Text ?? ""))
            {
                Nexus.Service.Platform.ServiceLog.Warn("[clipboard-helper] clipboard.setTextAndPaste: clipboard set failed, paste chord skipped (see the preceding [clipboard-win] line)");
                return Task.FromResult(HelperResult.Fail(env.Id, "paste failed: clipboard set failed"));
            }
            if (!SendPasteChord())
            {
                Nexus.Service.Platform.ServiceLog.Warn("[clipboard-helper] clipboard.setTextAndPaste: Ctrl+V SendInput rejected - check whether the foreground window runs elevated (UIPI blocks a Medium-integrity helper injecting into a High-integrity window)");
                return Task.FromResult(HelperResult.Fail(env.Id, "paste failed: keystroke injection rejected"));
            }
            return Task.FromResult(env.Ok());
        });
    }

    /// <summary>
    /// Runs in the user-session helper process, so SendInput lands on the
    /// interactive desktop. The same call from the Session-0 service lands on
    /// its own separate, invisible desktop instead, with no observable effect.
    /// Returns false when the OS rejects any keystroke of the chord.
    /// </summary>
    private static bool SendPasteChord()
    {
        Nexus.Service.Models.Peripherals.Keeb.MacroStroke V(string type) => new()
        {
            Key = "KeyV",
            Ctrl = true,
            Type = type,
        };
        return new Nexus.Service.Peripherals.Keeb.WindowsInputter().TrySend(
            new Nexus.Service.Models.Peripherals.Keeb.InputterBody { Strokes = { V("keydown"), V("keyup") } });
    }
}
#endif
