using System;
using System.Globalization;
using System.Linq;
using System.Xml.Linq;
#if WINDOWS
using System.Runtime.InteropServices;
using Microsoft.Win32;
using Nexus.Service.Diagnostics.EventLog;
#endif

namespace Nexus.Service.Diagnostics;

/// <summary>
/// Fast Startup state for the startup log: how the current boot began, and the
/// raw settings that decide whether Shut down hibernates the kernel instead of
/// powering off.
/// </summary>
internal static class BootTypeProbe
{
    private static readonly XNamespace Ns = "http://schemas.microsoft.com/win/2004/08/events/event";

    /// <summary>
    /// Raw values only: whether Fast Startup applies also depends on hibernation
    /// defaults and policy, so the boot type is the ground truth.
    /// </summary>
    internal static string Describe(string lastBoot, int? hiberboot, int? policyHiberboot, int? hibernate, int? hibernateDefault) =>
        $"last boot: {lastBoot}; HiberbootEnabled={Raw(hiberboot)} policy HiberbootEnabled={Raw(policyHiberboot)} "
        + $"HibernateEnabled={Raw(hibernate)} HibernateEnabledDefault={Raw(hibernateDefault)}";

    /// <summary>The newest Kernel-Boot 27 event, whose BootType says how Windows started.</summary>
    internal static string DescribeBootEvent(string? xml)
    {
        if (string.IsNullOrEmpty(xml)) return "no Kernel-Boot 27 event";
        try
        {
            var root = XDocument.Parse(xml).Root;
            var time = root?.Element(Ns + "System")?.Element(Ns + "TimeCreated")?.Attribute("SystemTime")?.Value;
            var raw = root?.Element(Ns + "EventData")?.Elements(Ns + "Data")
                .FirstOrDefault(d => (string?)d.Attribute("Name") == "BootType")?.Value;
            if (!int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var bootType))
            {
                return "Kernel-Boot 27 without a BootType";
            }
            var kind = bootType switch
            {
                0 => "full boot",
                1 => "fast startup",
                2 => "resume from hibernation",
                _ => "unknown",
            };
            return $"{kind} (BootType={bootType} at {time ?? "?"})";
        }
        catch (System.Xml.XmlException)
        {
            return "unreadable Kernel-Boot 27 event";
        }
    }

    private static string Raw(int? value) => value?.ToString(CultureInfo.InvariantCulture) ?? "unset";

#if WINDOWS
    private const string PowerKey = @"SYSTEM\CurrentControlSet\Control\Power";
    private const string BootEventQuery =
        "*[System[Provider[@Name='Microsoft-Windows-Kernel-Boot'] and EventID=27]]";

    internal static string Describe() => Describe(
        LastBoot(),
        ReadDword(@"SYSTEM\CurrentControlSet\Control\Session Manager\Power", "HiberbootEnabled"),
        ReadDword(@"SOFTWARE\Policies\Microsoft\Windows\System", "HiberbootEnabled"),
        ReadDword(PowerKey, "HibernateEnabled"),
        ReadDword(PowerKey, "HibernateEnabledDefault"));

    private static int? ReadDword(string keyPath, string name)
    {
        using var key = Registry.LocalMachine.OpenSubKey(keyPath);
        return key?.GetValue(name) is int value ? value : null;
    }

    private static string LastBoot()
    {
        using var results = WevtApi.EvtQuery(IntPtr.Zero, "System", BootEventQuery,
            WevtApi.EvtQueryChannelPath | WevtApi.EvtQueryReverseDirection);
        if (results.IsInvalid) return $"event query failed (error {Marshal.GetLastWin32Error()})";
        var buffer = new IntPtr[1];
        if (!WevtApi.EvtNext(results, 1u, buffer, 0u, 0u, out var returned) || returned == 0)
        {
            return DescribeBootEvent(null);
        }
        using var evt = new WevtApi.SafeEvtHandle(buffer[0], ownsHandle: true);
        return DescribeBootEvent(EventLogMonitor.Render(evt));
    }
#endif
}
