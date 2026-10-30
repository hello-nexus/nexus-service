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

    [Fact]
    public void Legacy_global_cap_is_read_from_a_prior_file_and_never_written_once_cleared()
    {
        var path = Path.Combine(Path.GetTempPath(), "nexus-fpscap-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            File.Copy(Path.Combine(AppContext.BaseDirectory, "Fixtures", "settings", "pre-per-device-fps-cap10.json"), path);
            using (var store = new JsonConfigStore(path))
            {
                Assert.Equal(10, store.Load().StreamedPanelFpsCap);
                store.Update(s => s.StreamedPanelFpsCap = null);
                store.FlushNow();
            }
            Assert.DoesNotContain("streamedPanelFpsCap", File.ReadAllText(path));
            using (var store = new JsonConfigStore(path))
            {
                Assert.Null(store.Load().StreamedPanelFpsCap);
            }
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
