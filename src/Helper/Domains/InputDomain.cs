#if WINDOWS
using System.Runtime.Versioning;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Nexus.Service.Models.Peripherals.Keeb;
using Nexus.Service.Serialization;

namespace Nexus.Service.Helper.Domains;

/// <summary>
/// Service-to-helper keystroke injection. The service runs LocalSystem in
/// Session 0, where SendInput has no interactive desktop to inject into, so
/// the user-session helper injects the strokes itself, in its own session.
/// Reuses <see cref="InputterBody"/> unchanged as the wire payload, the same
/// chord representation <see cref="Nexus.Service.Peripherals.Keeb.IInputterProvider"/>
/// consumes locally.
/// </summary>
public static class InputCommands
{
    public const string SendKeysType = "input.sendKeys";
    public const string InjectTouchType = "input.touch";

    /// <summary>Blocks until the helper has injected, so contacts reach Windows in order.</summary>
    public static bool InjectTouch(HelperRegistry registry, Nexus.Service.Models.Panel.TouchInjectBody body)
    {
        var conn = registry.GetAny();
        if (conn is null)
        {
            return false;
        }
        var result = conn.SendCommandAsync(
            InjectTouchType,
            body,
            AppJsonContext.Default.TouchInjectBody,
            timeoutMs: 1000).GetAwaiter().GetResult();
        return result.Ok;
    }

    public static async Task<bool> SendKeysAsync(HelperRegistry registry, InputterBody body, CancellationToken ct = default)
    {
        var conn = registry.GetAny();
        if (conn is null)
        {
            return false;
        }
        var result = await conn.SendCommandAsync(
            SendKeysType,
            body,
            AppJsonContext.Default.InputterBody,
            timeoutMs: 5000,
            ct: ct).ConfigureAwait(false);
        if (!result.Ok)
        {
            Nexus.Service.Platform.ServiceLog.Warn($"[input-win] helper keystroke send failed: {result.Error}");
        }
        return result.Ok;
    }
}

[SupportedOSPlatform("windows")]
public sealed class InputHandler
{
    public void Register(HelperHandlerRegistry registry)
    {
        registry.Register(InputCommands.SendKeysType, (env, _) =>
        {
            var ok = false;
            if (env.Payload is not null)
            {
                var body = JsonSerializer.Deserialize(env.Payload.Value, AppJsonContext.Default.InputterBody);
                if (body is not null)
                {
                    new Nexus.Service.Peripherals.Keeb.WindowsInputter().Send(body);
                    ok = true;
                }
            }
            return Task.FromResult(ok ? env.Ok() : HelperResult.Fail(env.Id, "send keys failed"));
        });
        registry.Register(InputCommands.InjectTouchType, (env, _) =>
        {
            var body = env.Payload is null
                ? null
                : JsonSerializer.Deserialize(env.Payload.Value, AppJsonContext.Default.TouchInjectBody);
            var ok = body is not null && TouchInjector.Inject(body);
            return Task.FromResult(ok ? env.Ok() : HelperResult.Fail(env.Id, "touch injection failed"));
        });
    }
}
#endif
