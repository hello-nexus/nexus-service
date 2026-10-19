using System;
using System.IO;
using Nexus.Service.Devices;
using Nexus.Service.Persistence;
using Xunit;

namespace Nexus.Service.Tests.LianLiFlex;

public class FlexSettingsAndPolicyTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "nexus-flex-" + Guid.NewGuid().ToString("N")[..8]);

    public FlexSettingsAndPolicyTests()
    {
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch
        {
            // best effort
        }
    }

    [Fact]
    public void An_older_document_loads_with_empty_flex_dictionaries()
    {
        var path = Path.Combine(_dir, "settings.json");
        File.WriteAllText(path, $$"""
        { "schemaVersion": {{NexusSettings.CurrentSchemaVersion}}, "theme": { "themeMode": "dark" } }
        """);
        var s = new JsonConfigStore(path).Load();
        Assert.Equal("dark", s.Theme.ThemeMode);
        Assert.Empty(s.Devices.FlexUnits);
        Assert.Empty(s.Devices.FlexLcdScreens);
    }

    [Fact]
    public void Flex_unit_settings_round_trip_through_the_store()
    {
        var path = Path.Combine(_dir, "settings.json");
        using (var store = new JsonConfigStore(path))
        {
            store.Update(s => s.Devices.FlexUnits["6D1007F517E1"] = new LianLiFlexUnitSettings
            {
                Mode = LianLiFlexUnitSettings.ModePerZone,
                Speed = 1,
                UpDownSwap = true,
                SortIndex = 2,
                Colors = new() { "#112233" },
                Zones = new() { new LianLiFlexZoneSettings { Mode = "meteor", Brightness = 2, Colors = new() { "#ff0000" } } },
                PackageSignature = "sig",
                PackageEffectIndex = "00AABBCC",
            });
            store.FlushNow();
        }
        var loaded = new JsonConfigStore(path).Load().Devices.FlexUnits["6D1007F517E1"];
        Assert.Equal(LianLiFlexUnitSettings.ModePerZone, loaded.Mode);
        Assert.True(loaded.UpDownSwap);
        Assert.Equal(2, loaded.SortIndex);
        Assert.Equal("meteor", Assert.Single(loaded.Zones).Mode);
        Assert.Equal("00AABBCC", loaded.PackageEffectIndex);
    }

    [Fact]
    public void The_flex_handler_is_experimental_and_tied_to_the_vendor_app()
    {
        Assert.True(DeviceControlPolicy.IsExperimental("lianli-flex"));
        Assert.Equal("lian-li-l-connect", DeviceControlPolicy.ConflictAppFor("lianli-flex"));
    }
}
