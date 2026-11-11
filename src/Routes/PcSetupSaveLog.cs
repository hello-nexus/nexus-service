using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Nexus.Service.Lighting.Scene;
using Nexus.Service.Platform;

namespace Nexus.Service.Routes;

/// <summary>One INFO line per setup write, so a log shows which client saved what. Never throws.</summary>
public static class PcSetupSaveLog
{
    private const int KeyLimit = 200;
    private const int ConfirmKeysShown = 8;
    private static readonly string[] ConfirmPrefixes = ["confirm:", "cooler-confirm:"];

    public static void Write(HttpContext ctx, string route, string outcome, int? baseRev, int? newRev, PcSetup? setup)
    {
        try
        {
            var layout = setup?.Resolved.CaseLayout;
            var line = $"[pc-setup] {route} {outcome} rev {Num(baseRev)}->{Num(newRev)} bytes={ctx.Request.ContentLength?.ToString(CultureInfo.InvariantCulture) ?? "?"}"
                + $" mount={setup?.Case.RadiatorMount ?? "-"} {CoolerSummary(layout)}"
                + $" confirm={ConfirmSummary(setup?.Parts)} inputKey={Truncate(InputKey(layout))} {Caller(ctx)}";
            ServiceLog.Info(line);
        }
        catch (Exception e)
        {
            ServiceLog.Info($"[pc-setup] {route} {outcome} (log failed: {e.GetType().Name})");
        }
    }

    /// <summary>The cooler boxes of the case layout (record units are tenths of a millimeter): "cooler WxDxH", or "pump .. + rad ..", "none", "unreadable".</summary>
    public static string CoolerSummary(JsonElement? layout)
    {
        try
        {
            if (layout is not { ValueKind: JsonValueKind.Object } l)
            {
                return "cooler none";
            }
            if (!l.TryGetProperty("boxes", out var boxes) || boxes.ValueKind != JsonValueKind.Array)
            {
                return "cooler none";
            }
            var parts = new List<string>();
            foreach (var box in boxes.EnumerateArray())
            {
                if (box.ValueKind != JsonValueKind.Object || Str(box, "slot") != "cooler" || Str(box, "role") != "part")
                {
                    continue;
                }
                var size = Size(box);
                if (size is null)
                {
                    continue;
                }
                var dims = box.TryGetProperty("dims", out var d) && d.ValueKind == JsonValueKind.Array
                    ? d.EnumerateArray().Select(x => Str(x, "key")).ToList()
                    : new List<string?>();
                var label = dims.Contains("radiator") ? "rad" : dims.Count == 0 ? "pump" : "cooler";
                parts.Add($"{label} {size}");
            }
            return parts.Count == 0 ? "cooler none" : string.Join(" + ", parts);
        }
        catch (Exception)
        {
            return "cooler unreadable";
        }
    }

    /// <summary>"n" or "n[key,key]" for the setup.parts.devices keys that start with a confirm prefix.</summary>
    public static string ConfirmSummary(JsonElement? parts)
    {
        try
        {
            if (parts is not { ValueKind: JsonValueKind.Object } p
                || !p.TryGetProperty("devices", out var devices)
                || devices.ValueKind != JsonValueKind.Object)
            {
                return "0";
            }
            var keys = devices.EnumerateObject()
                .Select(x => x.Name)
                .Where(n => ConfirmPrefixes.Any(prefix => n.StartsWith(prefix, StringComparison.Ordinal)))
                .ToList();
            return keys.Count == 0
                ? "0"
                : $"{keys.Count}[{string.Join(",", keys.Take(ConfirmKeysShown).Select(k => Truncate(k, 60)))}]";
        }
        catch (Exception)
        {
            return "?";
        }
    }

    private static string? InputKey(JsonElement? layout) =>
        layout is { ValueKind: JsonValueKind.Object } l && l.TryGetProperty("inputKey", out var k) && k.ValueKind == JsonValueKind.String
            ? k.GetString()
            : null;

    private static string Caller(HttpContext ctx)
    {
        var headers = ctx.Request.Headers;
        var origin = Host(headers.Referer.ToString()) ?? Host(headers.Origin.ToString()) ?? "-";
        var headless = Uri.TryCreate(headers.Referer.ToString(), UriKind.Absolute, out var r)
            ? r.Query.Contains("headless", StringComparison.OrdinalIgnoreCase) ? "yes" : "no"
            : "unknown";
        var ua = headers.UserAgent.ToString();
        var token = ua.Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? "-";
        var loopback = ctx.Connection.RemoteIpAddress is { } ip && System.Net.IPAddress.IsLoopback(ip) ? "yes" : "no";
        return $"from={origin} headless={headless} ua={Truncate(token, 40)} loopback={loopback}";
    }

    private static string? Host(string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var u) ? u.Host : null;

    private static string? Str(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static string? Size(JsonElement box)
    {
        if (!box.TryGetProperty("size", out var s) || s.ValueKind != JsonValueKind.Array || s.GetArrayLength() != 3)
        {
            return null;
        }
        var dims = new List<string>();
        foreach (var v in s.EnumerateArray())
        {
            if (v.ValueKind != JsonValueKind.Number)
            {
                return null;
            }
            dims.Add(Math.Round(v.GetDouble() / 10).ToString(CultureInfo.InvariantCulture));
        }
        return string.Join("x", dims);
    }

    private static string Num(int? v) => v?.ToString(CultureInfo.InvariantCulture) ?? "?";

    private static string Truncate(string? s, int max = KeyLimit) =>
        s is null ? "-" : s.Length <= max ? s : s[..max] + "...";
}
