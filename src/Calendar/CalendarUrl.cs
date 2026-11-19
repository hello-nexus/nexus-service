using System;
using System.Net;
using System.Net.Sockets;

namespace Nexus.Service.Calendar;

/// <summary>Validation of a user-supplied feed address, and the public-address rule the fetcher enforces on every connection.</summary>
public static class CalendarUrl
{
    public const int MaxLength = 2048;

    /// <summary>webcal(s):// becomes https://; only http and https to a public host are accepted.</summary>
    public static bool TryNormalize(string? raw, out Uri uri)
    {
        uri = null!;
        var text = (raw ?? "").Trim();
        if (text.Length == 0 || text.Length > MaxLength)
        {
            return false;
        }
        if (text.StartsWith("webcal://", StringComparison.OrdinalIgnoreCase))
        {
            text = "https://" + text.Substring("webcal://".Length);
        }
        else if (text.StartsWith("webcals://", StringComparison.OrdinalIgnoreCase))
        {
            text = "https://" + text.Substring("webcals://".Length);
        }
        if (!Uri.TryCreate(text, UriKind.Absolute, out var parsed))
        {
            return false;
        }
        if (parsed.Scheme != Uri.UriSchemeHttps && parsed.Scheme != Uri.UriSchemeHttp)
        {
            return false;
        }
        if (!IsPublicHostName(parsed.Host))
        {
            return false;
        }
        uri = parsed;
        return true;
    }

    internal static bool IsPublicHostName(string host)
    {
        if (host.Length == 0)
        {
            return false;
        }
        if (IPAddress.TryParse(host.Trim('[', ']'), out var ip))
        {
            return IsPublicAddress(ip);
        }
        var h = host.ToLowerInvariant().TrimEnd('.');
        if (h == "localhost" || h.EndsWith(".localhost", StringComparison.Ordinal)
            || h.EndsWith(".local", StringComparison.Ordinal) || h.EndsWith(".internal", StringComparison.Ordinal)
            || h.EndsWith(".lan", StringComparison.Ordinal) || h.EndsWith(".home.arpa", StringComparison.Ordinal))
        {
            return false;
        }
        return h.Contains('.');
    }

    public static bool IsPublicAddress(IPAddress ip)
    {
        if (ip.IsIPv4MappedToIPv6)
        {
            ip = ip.MapToIPv4();
        }
        if (IPAddress.IsLoopback(ip) || ip.Equals(IPAddress.Any) || ip.Equals(IPAddress.IPv6Any))
        {
            return false;
        }
        if (ip.AddressFamily == AddressFamily.InterNetworkV6)
        {
            var b6 = ip.GetAddressBytes();
            if (ip.IsIPv6LinkLocal || ip.IsIPv6SiteLocal || ip.IsIPv6Multicast || (b6[0] & 0xFE) == 0xFC)
            {
                return false;
            }
            // Forms that carry an IPv4 address: NAT64, IPv4-compatible and 6to4. Judge the embedded address.
            var nat64 = b6[0] == 0x00 && b6[1] == 0x64 && b6[2] == 0xFF && b6[3] == 0x9B && IsZero(b6, 4, 12);
            if (nat64 || IsZero(b6, 0, 12))
            {
                return IsPublicAddress(new IPAddress(new[] { b6[12], b6[13], b6[14], b6[15] }));
            }
            if (b6[0] == 0x20 && b6[1] == 0x02)
            {
                return IsPublicAddress(new IPAddress(new[] { b6[2], b6[3], b6[4], b6[5] }));
            }
            return true;
        }
        var b = ip.GetAddressBytes();
        if (b[0] == 0 || b[0] == 10 || b[0] >= 224)
        {
            return false;
        }
        if (b[0] == 127 || (b[0] == 169 && b[1] == 254) || (b[0] == 192 && b[1] == 168))
        {
            return false;
        }
        if (b[0] == 172 && b[1] >= 16 && b[1] <= 31)
        {
            return false;
        }
        if (b[0] == 100 && b[1] >= 64 && b[1] <= 127)
        {
            return false;
        }
        if (b[0] == 198 && (b[1] == 18 || b[1] == 19))
        {
            return false;
        }
        if (b[0] == 192 && b[1] == 0 && b[2] == 0)
        {
            return false;
        }
        return true;
    }

    private static bool IsZero(byte[] bytes, int from, int to)
    {
        for (var i = from; i < to; i++)
        {
            if (bytes[i] != 0)
            {
                return false;
            }
        }
        return true;
    }
}
