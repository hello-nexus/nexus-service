using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Nexus.Service.Conflicts;
using Nexus.Service.Devices;
using Xunit;

namespace Nexus.Service.Tests.Hid;

public class WholeBusHidGuardTests
{
    private static string SrcDir()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, "src", "Peripherals", "Hid")))
        {
            dir = dir.Parent;
        }
        Assert.NotNull(dir);
        return Path.Combine(dir!.FullName, "src");
    }

    // Whole-bus enumeration opens every HID interface on the PC. Only the enumerator
    // implementations, the DI registration, the one-shot startup dump and the change-gated LampArray count may name it.
    [Fact]
    public void Only_allowlisted_files_reference_whole_bus_hid_enumeration()
    {
        var allowed = new HashSet<string>(StringComparer.Ordinal)
        {
            "Peripherals/Hid/IHidEnumerator.cs",
            "Peripherals/Hid/WindowsHidEnumerator.cs",
            "Peripherals/Hid/MacHidEnumerator.cs",
            "Peripherals/Hid/LinuxHidEnumerator.cs",
            "Peripherals/Hid/StubHidEnumerator.cs",
            "DependencyInjection/NexusServiceCollectionExtensions.cs",
            "Diagnostics/StartupDiagnosticsDumpService.cs",
            "Conflicts/WindowsDynamicLighting.cs",
        };
        var src = SrcDir();
        var offenders = Directory.EnumerateFiles(src, "*.cs", SearchOption.AllDirectories)
            .Select(f => Path.GetRelativePath(src, f).Replace('\\', '/'))
            .Where(rel => !allowed.Contains(rel))
            .Where(rel =>
            {
                var text = File.ReadAllText(Path.Combine(src, rel));
                return text.Contains("FindAllForDiagnostics", StringComparison.Ordinal)
                    || text.Contains("FindAllWithoutSerials", StringComparison.Ordinal)
                    || text.Contains("IHidWholeBusEnumerator", StringComparison.Ordinal);
            })
            .ToList();
        Assert.Empty(offenders);
    }

    [Fact]
    public void Count_walks_once_while_the_bus_signature_is_unchanged()
    {
        var gate = new BusGatedCount();
        var walks = 0;
        int Walk() { walks++; return 2; }

        Assert.Equal(2, gate.Get("a", Walk));
        Assert.Equal(2, gate.Get("a", Walk));
        Assert.Equal(1, walks);
    }

    [Fact]
    public void Count_walks_again_on_a_bus_change_and_after_invalidate()
    {
        var gate = new BusGatedCount();
        var walks = 0;
        int Walk() => ++walks;

        Assert.Equal(1, gate.Get("a", Walk));
        Assert.Equal(2, gate.Get("b", Walk));
        gate.Invalidate();
        Assert.Equal(3, gate.Get("b", Walk));
    }

    [Fact]
    public void Count_with_no_bus_source_walks_once()
    {
        var gate = new BusGatedCount();
        var walks = 0;
        int Walk() => ++walks;

        gate.Get(null, Walk);
        gate.Get(null, Walk);
        Assert.Equal(1, walks);
    }

    [Fact]
    public void Bus_signature_ignores_order_and_tracks_plug_events()
    {
        var a = new UsbDeviceEntry { VendorId = 1, ProductId = 2, Serial = "x", Location = "p1" };
        var b = new UsbDeviceEntry { VendorId = 3, ProductId = 4, Serial = "y", Location = "p2" };

        Assert.Equal(WindowsDynamicLighting.UsbBusSignature(new[] { a, b }), WindowsDynamicLighting.UsbBusSignature(new[] { b, a }));
        Assert.NotEqual(WindowsDynamicLighting.UsbBusSignature(new[] { a, b }), WindowsDynamicLighting.UsbBusSignature(new[] { a }));
    }
}
