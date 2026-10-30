using System;
using System.IO;
using Nexus.Service.Models.Panel;
using Nexus.Service.Panel;
using Nexus.Service.Panel.Streams;
using Nexus.Service.Persistence;
using Xunit;

namespace Nexus.Service.Tests.Panel.Streams;

public sealed class StreamFpsCapTests
{
    [Theory]
    [InlineData(60, 30, 30)]
    [InlineData(20, 30, 20)]
    [InlineData(15, 15, 15)]
    [InlineData(60, null, 60)]
    public void Effective_never_raises_the_model_rate(int model, int? cap, int expected) =>
        Assert.Equal(expected, StreamFpsCap.Effective(model, cap));

    // The fixture is the settings file the code that carried the global cap wrote (cap 10); 30 was its default.
    [Theory]
    [InlineData(10)]
    [InlineData(30)]
    public void A_prior_file_with_the_global_cap_loads_and_every_device_starts_uncapped(int legacyCap)
    {
        var path = Path.Combine(Path.GetTempPath(), "nexus-fpscap-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            var json = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "settings", "pre-per-device-fps-cap10.json"))
                .Replace("\"streamedPanelFpsCap\": 10", $"\"streamedPanelFpsCap\": {legacyCap}");
            Assert.Contains($"\"streamedPanelFpsCap\": {legacyCap}", json);
            File.WriteAllText(path, json);
            using (var store = new JsonConfigStore(path))
            {
                var registry = new PanelDeviceRegistry(store);
                var first = registry.Allocate("Streamed", null);
                var second = registry.Allocate("Kiosk", null);
                Assert.Null(registry.Get(first.Id)!.FpsCap);
                Assert.Null(registry.Get(second.Id)!.FpsCap);
                Assert.All(store.Load().PanelDevices.Values, d => Assert.Null(d.FpsCap));
                store.Update(_ => { });
                store.FlushNow();
            }
            Assert.DoesNotContain("streamedPanelFpsCap", File.ReadAllText(path));
        }
        finally
        {
            try { File.Delete(path); } catch { }
            try { File.Delete(path + ".bak"); } catch { }
        }
    }

    [Fact]
    public void Patch_clamps_a_record_cap_and_zero_clears_it()
    {
        var path = Path.Combine(Path.GetTempPath(), "nexus-fpscap-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            using var store = new JsonConfigStore(path);
            var registry = new PanelDeviceRegistry(store);
            var id = registry.Allocate("Panel", null).Id;

            Assert.Equal(PanelDeviceRegistry.MaxFpsCap, registry.Patch(id, new PanelDevicePatch { FpsCap = 5000 })!.FpsCap);
            Assert.Equal(15, registry.Patch(id, new PanelDevicePatch { FpsCap = 15 })!.FpsCap);
            Assert.Equal(15, registry.Patch(id, new PanelDevicePatch())!.FpsCap);
            Assert.Null(registry.Patch(id, new PanelDevicePatch { FpsCap = 0 })!.FpsCap);
        }
        finally
        {
            try { File.Delete(path); } catch { }
            try { File.Delete(path + ".bak"); } catch { }
        }
    }
}
