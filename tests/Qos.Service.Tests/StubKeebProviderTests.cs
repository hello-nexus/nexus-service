using System.Collections.Generic;
using Qos.Service.Devices;
using Qos.Service.Devices.Detection;
using Qos.Service.Devices.Handlers;
using Qos.Service.Models.Common;
using Qos.Service.Models.Peripherals.Keeb;
using Qos.Service.Peripherals.Keeb;
using Qos.Service.Persistence;

namespace Qos.Service.Tests;

/// <summary>
/// Exercises the persistence-only stub provider: every setter writes through to
/// <see cref="IConfigStore"/> and reads round-trip the persisted value.
/// Connectivity is reported via DeviceManager so the UI's offline state is honest
/// even before the HID driver lands.
/// </summary>
public class StubKeebProviderTests : IDisposable
{
    private readonly string _tempDir;
    private readonly string _settingsPath;

    public StubKeebProviderTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "qos-keeb-stub-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_tempDir);
        _settingsPath = Path.Combine(_tempDir, "settings.json");
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); } catch { }
    }

    private (StubKeebProvider provider, JsonConfigStore store) Build(params UsbDeviceEntry[] devices)
    {
        var store = new JsonConfigStore(_settingsPath);
        var manager = new DeviceManager(new IDeviceHandler[] { new KeebHandler() }, new FixedUsbEnumerator(devices));
        var provider = new StubKeebProvider(store, manager);
        return (provider, store);
    }

    [Fact]
    public void GetState_ReportsDisconnectedWhenNoKeebUsbEntry()
    {
        var (provider, store) = Build();
        try
        {
            var state = provider.GetState();
            Assert.False(state.IsConnected);
            Assert.Equal(0, state.Layer);
            Assert.Empty(state.Keys);
        }
        finally { store.Dispose(); }
    }

    [Fact]
    public void GetState_ReportsConnectedWhenKeebUsbEntryPresent()
    {
        var (provider, store) = Build(new UsbDeviceEntry
        {
            VendorId = 0x3402,
            ProductId = 0x0300,
            Name = "HYTE Keeb TKL",
        });
        try
        {
            var state = provider.GetState();
            Assert.True(state.IsConnected);
        }
        finally { store.Dispose(); }
    }

    [Fact]
    public void SetLayerKey_PersistsAndRoundTripsViaGetLayer()
    {
        var (provider, store) = Build();
        try
        {
            provider.SetLayerKey(0, new SetLayerKeyBody
            {
                X = 2, Y = 5, Func = "Macro1", Mode = "MacroKey", Input = 1,
            });

            var keys = provider.GetLayer(0);
            Assert.True(keys.Count > 2);
            Assert.True(keys[2].Count > 5);
            Assert.Equal("Macro1", keys[2][5].Function);
            Assert.Equal("MacroKey", keys[2][5].Mode);
            Assert.Equal(1, keys[2][5].Input);
        }
        finally { store.Dispose(); }
    }

    [Fact]
    public void ResetLayer_ClearsCachedLayerSnapshot()
    {
        var (provider, store) = Build();
        try
        {
            provider.SetLayerKey(0, new SetLayerKeyBody { X = 0, Y = 0, Func = "A", Mode = "StandardKey" });
            Assert.NotEmpty(provider.GetLayer(0));
            provider.ResetLayer(0);
            Assert.Empty(provider.GetLayer(0));
        }
        finally { store.Dispose(); }
    }

    [Fact]
    public void SetPassiveLighting_PersistsAndFlowsBackThroughGetSettings()
    {
        var (provider, store) = Build();
        try
        {
            provider.SetPassiveLighting(new SetPassiveLightingBody
            {
                KeyReactive = true,
                KeyReactiveMask = true,
                KeyReactiveMode = "Ripple",
                KeyReactiveColor = new RGBA { R = 10, G = 20, B = 30, A = 255 },
            });

            var settings = provider.GetSettings();
            Assert.True(settings.KeyReactive);
            Assert.True(settings.KeyReactiveMask);
            Assert.Equal("Ripple", settings.KeyReactiveMode);
            Assert.Equal(10, settings.KeyReactiveColor.R);
            Assert.Equal(20, settings.KeyReactiveColor.G);
            Assert.Equal(30, settings.KeyReactiveColor.B);
        }
        finally { store.Dispose(); }
    }

    [Fact]
    public void SetFirmwareLighting_DoesNotTouchPassiveLightingFields()
    {
        var (provider, store) = Build();
        try
        {
            // Seed passive lighting.
            provider.SetPassiveLighting(new SetPassiveLightingBody
            {
                KeyReactive = true,
                KeyReactiveMode = "HorizontalLine",
                KeyReactiveColor = new RGBA { R = 99 },
            });
            // Touch firmware lighting in isolation.
            provider.SetFirmwareLighting(new SetFirmwareLightingBody
            {
                AnimationMode = "Wave",
                Speed = "Rapid",
                Direction = "BottomToTop",
                Brightness = 33,
                KeyIndicator = true,
            });

            var settings = provider.GetSettings();
            Assert.Equal("Wave", settings.AnimationMode);
            Assert.Equal(33, settings.Brightness);
            Assert.True(settings.KeyIndicator);
            // Passive fields survived the firmware-side write — that's the
            // whole point of splitting these two bodies.
            Assert.True(settings.KeyReactive);
            Assert.Equal("HorizontalLine", settings.KeyReactiveMode);
            Assert.Equal(99, settings.KeyReactiveColor.R);
        }
        finally { store.Dispose(); }
    }

    [Fact]
    public void SetMacro_RoundTripsAcrossGetMacro()
    {
        var (provider, store) = Build();
        try
        {
            var written = provider.SetMacro(3, new SetMacroBody
            {
                Keys = new List<MacroKey>
                {
                    new() { Key = "A", Duration = 50, Type = "Make" },
                    new() { Key = "A", Duration = 50, Type = "Break" },
                },
            });
            Assert.Equal(3, written.Index);
            Assert.Equal(2, written.Keys.Count);

            var read = provider.GetMacro(3);
            Assert.Equal(2, read.Keys.Count);
            Assert.Equal("A", read.Keys[0].Key);
            Assert.Equal(50, read.Keys[0].Duration);
            Assert.Equal("Make", read.Keys[0].Type);
        }
        finally { store.Dispose(); }
    }
}
