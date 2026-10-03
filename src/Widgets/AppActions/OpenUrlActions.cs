using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using Microsoft.Extensions.DependencyInjection;
using Nexus.Service.Actions;

namespace Nexus.Service.Widgets.AppActions;

/// <summary>
/// Host action letting an SDK app hand a link to the user's browser or mail
/// client. Sandboxed workers have no other way out to a site, so the gate is
/// narrow: an https link only to a host the app's own manifest already
/// allowlists for fetching, or a plain mailto address.
/// </summary>
public static partial class OpenUrlActions
{
    public const int MaxUrlLength = 2048;

    // Unreserved, gen-delims, sub-delims and the percent sign of RFC 3986.
    private const string UrlPunctuation = "-._~:/?#[]@!$&'()*+,;=%";

    // The shared dispatch limiter is sized for in-process reads and writes.
    // A launch opens a window in the user's face and cannot be taken back, so
    // this action carries its own far tighter bucket.
    private static readonly LaunchLimiter Launches = new();

    public static void RegisterAll(AppActionRegistry registry)
    {
        registry.Register("system.openUrl", async (services, args, ct) =>
        {
            ct.ThrowIfCancellationRequested();
            var appId = AppActionHelpers.Str(args, "__appId") ?? "";
            var url = AppActionHelpers.Str(args, "url") ?? "";

            // The allowlist comes from the installed manifest, never from the
            // caller's args, so a tampered worker cannot widen its own reach.
            var apps = services.GetRequiredService<AppRegistry>();
            IReadOnlyList<string> allowlist = apps.TryGet(appId, out var entry)
                ? entry.Manifest.Capabilities.NetFetch
                : Array.Empty<string>();

            if (!TryValidate(url, allowlist, out var target, out _))
            {
                return AppActionHelpers.Ack(false, "url not permitted");
            }
            // A manifest entry is not proof the host is public. The proxy
            // refuses the non-routable hosts of that same allowlist, and a
            // browser pointed at one reaches services only this box can see.
            if (target.StartsWith("https:", StringComparison.Ordinal) &&
                AppProxyService.IsPrivateOrReservedAddress(new Uri(target).Host))
            {
                return AppActionHelpers.Ack(false, "url not permitted");
            }
            if (!Launches.TryAcquire(appId, DateTime.UtcNow))
            {
                return AppActionHelpers.Ack(false, "rate limit exceeded");
            }

            Nexus.Service.Telemetry.AppLinkTelemetry.Capture(services.GetRequiredService<Nexus.Service.Telemetry.ITelemetry>(), apps, appId, target);
            var opened = await services.GetRequiredService<SystemActions>()
                .LaunchUrlAsync(target).ConfigureAwait(false);
            return opened.Error
                ? AppActionHelpers.Ack(false, "failed to open url")
                : AppActionHelpers.Ack(true);
        });
    }

    public static IReadOnlyList<string> AllActions => new[] { "system.openUrl" };

    /// <summary>
    /// Decides whether <paramref name="url"/> may be handed to the platform
    /// shell on behalf of an app whose manifest allowlists
    /// <paramref name="allowlist"/>, and yields the exact string to open.
    /// </summary>
    internal static bool TryValidate(string? url, IReadOnlyList<string>? allowlist, out string target, out string reason)
    {
        target = "";
        var trimmed = url?.Trim() ?? "";
        if (trimmed.Length == 0)
        {
            reason = "url is required";
            return false;
        }
        if (trimmed.Length > MaxUrlLength)
        {
            reason = "url is too long";
            return false;
        }
        foreach (var ch in trimmed)
        {
            // Only what a URL may carry unencoded, which drops whitespace,
            // control and non-ASCII characters. Percent escapes pass here and
            // are judged where something would decode them.
            if (!char.IsAsciiLetterOrDigit(ch) && !UrlPunctuation.Contains(ch))
            {
                reason = "url has an unsupported character";
                return false;
            }
        }
        if (!Uri.TryCreate(trimmed, UriKind.Absolute, out var parsed))
        {
            reason = "url must be absolute";
            return false;
        }

        if (string.Equals(parsed.Scheme, "mailto", StringComparison.OrdinalIgnoreCase))
        {
            if (!IsPlainMailto(trimmed[(trimmed.IndexOf(':') + 1)..]))
            {
                reason = "mailto must be one address with subject or body only";
                return false;
            }
            target = trimmed;
            reason = "";
            return true;
        }

        if (!string.Equals(parsed.Scheme, "https", StringComparison.OrdinalIgnoreCase))
        {
            reason = "scheme is not permitted";
            return false;
        }
        if (!string.IsNullOrEmpty(parsed.UserInfo))
        {
            reason = "url carries userinfo";
            return false;
        }
        if (!AppProxyService.HostInAllowlist(parsed.Host, allowlist ?? Array.Empty<string>()))
        {
            reason = $"host '{parsed.Host}' is not in the manifest's net.fetch allowlist";
            return false;
        }

        target = parsed.AbsoluteUri;
        reason = "";
        return true;
    }

    private static bool IsPlainMailto(string rest)
    {
        // A mailto carries no fragment, so a hash is only ever an attempt to
        // hide something past the address.
        if (rest.Length == 0 || rest.Contains('#')) return false;
        var mark = rest.IndexOf('?');
        var address = mark < 0 ? rest : rest[..mark];
        // The address reaches the mail client percent-decoded, and no support
        // address needs an escape, so one is only ever hiding a character the
        // gate above would have refused.
        if (address.Contains('%')) return false;
        if (!MailAddress().IsMatch(address)) return false;
        if (mark < 0) return true;

        foreach (var field in rest[(mark + 1)..].Split('&'))
        {
            if (field.Length == 0) return false;
            var eq = field.IndexOf('=');
            var key = eq < 0 ? field : field[..eq];
            if (!string.Equals(key, "subject", StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(key, "body", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
            if (eq >= 0 && !IsSafeDecodedText(field[(eq + 1)..])) return false;
        }
        return true;
    }

    /// <summary>
    /// Guards what the mail client sees after it percent-decodes a field: a
    /// line break turns a subject into an extra message header, and a quote
    /// is what closes an argument in a handler's command template.
    /// </summary>
    private static bool IsSafeDecodedText(string value)
    {
        string decoded;
        try
        {
            decoded = Uri.UnescapeDataString(value);
        }
        catch (UriFormatException)
        {
            return false;
        }
        foreach (var ch in decoded)
        {
            if (char.IsControl(ch) || ch == '"') return false;
        }
        return true;
    }

    // Anchored, so a second address (which needs a second @) cannot ride along.
    [GeneratedRegex(@"^[^@\s]+@[^@\s]+\.[^@\s]+$", RegexOptions.CultureInvariant)]
    private static partial Regex MailAddress();

    /// <summary>Rolling per-app cap on how many links one app may open.</summary>
    internal sealed class LaunchLimiter
    {
        public const int MaxPerWindow = 5;
        public static readonly TimeSpan Window = TimeSpan.FromSeconds(10);

        private readonly ConcurrentDictionary<string, Queue<DateTime>> _buckets = new(StringComparer.Ordinal);

        public bool TryAcquire(string appId, DateTime now)
        {
            var bucket = _buckets.GetOrAdd(appId, static _ => new Queue<DateTime>());
            lock (bucket)
            {
                while (bucket.Count > 0 && now - bucket.Peek() >= Window)
                {
                    bucket.Dequeue();
                }
                if (bucket.Count >= MaxPerWindow) return false;
                bucket.Enqueue(now);
                return true;
            }
        }
    }
}
