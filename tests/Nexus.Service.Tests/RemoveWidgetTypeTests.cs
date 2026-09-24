using System;
using System.Collections.Generic;
using System.Linq;
using Nexus.Service.Models.Panel;
using Nexus.Service.Panel;
using Nexus.Service.Persistence;
using Xunit;

namespace Nexus.Service.Tests;

/// <summary>
/// Uninstalling an app retires its placements server-side. A client keeps an
/// app id it cannot resolve (it reads as an install it has not seen yet), so
/// nothing else retires one.
/// </summary>
public class RemoveWidgetTypeTests
{
    private const string InaType = "app:com.hellonexus.ina";

    private sealed class MemoryConfigStore : IConfigStore
    {
        public NexusSettings Settings { get; } = new();
        public int Writes { get; private set; }
        public string SettingsPath => ":memory:";
        public NexusSettings Load() => Settings;
        public void Update(Action<NexusSettings> mutator) { Writes++; mutator(Settings); OnChanged?.Invoke(); }
        public void Reload() { }
        public void FlushNow() { }
        public event Action? OnChanged;
    }

    private static PanelDeviceRecord Record(string id, string surface, params string[] types) => new()
    {
        Id = id,
        Capabilities = new PanelDeviceCapabilities { Surface = surface },
        Layout = new PanelLayoutDto
        {
            Surface = surface,
            Pages = new List<PanelPageDto>
            {
                new()
                {
                    Id = "p1",
                    Widgets = types.Select((t, i) => new PanelWidgetDto { Id = $"w{i}", Type = t, Size = "2x2" }).ToList(),
                },
            },
        },
    };

    private static List<string> TypesOn(MemoryConfigStore store, string deviceId) =>
        store.Settings.PanelDevices[deviceId].Layout!.Pages.SelectMany(p => p.Widgets).Select(w => w.Type).ToList();

    [Fact]
    public void PurgesEveryRecordHoldingTheTypeAndNamesThem()
    {
        var store = new MemoryConfigStore();
        store.Settings.PanelDevices["y70-1"] = Record("y70-1", PanelSurfaces.Y70, "clock", InaType);
        store.Settings.PanelDevices["phone-1"] = Record("phone-1", PanelSurfaces.Phone, InaType);
        var registry = new PanelDeviceRegistry(store);

        var changed = registry.RemoveWidgetType(InaType);

        Assert.Equal(new[] { "phone-1", "y70-1" }, changed.OrderBy(id => id).ToArray());
        Assert.Equal(new[] { "clock" }, TypesOn(store, "y70-1"));
        Assert.Empty(TypesOn(store, "phone-1"));
    }

    [Fact]
    public void LeavesARecordWithoutTheTypeUnreported()
    {
        var store = new MemoryConfigStore();
        store.Settings.PanelDevices["y70-1"] = Record("y70-1", PanelSurfaces.Y70, InaType);
        store.Settings.PanelDevices["q60-1"] = Record("q60-1", PanelSurfaces.Q60, "clock");
        var registry = new PanelDeviceRegistry(store);

        Assert.Equal(new[] { "y70-1" }, registry.RemoveWidgetType(InaType).ToArray());
        Assert.Equal(new[] { "clock" }, TypesOn(store, "q60-1"));
    }

    // Update marks settings dirty and fans out to every subscriber, so a miss
    // must not reach it.
    [Fact]
    public void NoMatchWritesNothing()
    {
        var store = new MemoryConfigStore();
        store.Settings.PanelDevices["y70-1"] = Record("y70-1", PanelSurfaces.Y70, "clock");
        var registry = new PanelDeviceRegistry(store);

        Assert.Empty(registry.RemoveWidgetType(InaType));
        Assert.Equal(0, store.Writes);
    }

    [Fact]
    public void ARecordStillOnTheStarterLayoutIsSkipped()
    {
        var store = new MemoryConfigStore();
        store.Settings.PanelDevices["y70-1"] = new PanelDeviceRecord
        {
            Id = "y70-1",
            Capabilities = new PanelDeviceCapabilities { Surface = PanelSurfaces.Y70 },
        };
        var registry = new PanelDeviceRegistry(store);

        Assert.Empty(registry.RemoveWidgetType(InaType));
        Assert.Equal(0, store.Writes);
    }

    // The mark names a widget id; leaving it pointing at a purged widget strands
    // the record in a state only a client layout write repairs.
    [Fact]
    public void ClearsAnImmersiveMarkLeftDangling()
    {
        var store = new MemoryConfigStore();
        var record = Record("y70-1", PanelSurfaces.Y70, "clock", InaType);
        record.Layout!.ImmersiveOnLoadWidgetId = record.Layout.Pages[0].Widgets[1].Id;
        store.Settings.PanelDevices["y70-1"] = record;
        var registry = new PanelDeviceRegistry(store);

        registry.RemoveWidgetType(InaType);

        Assert.Null(store.Settings.PanelDevices["y70-1"].Layout!.ImmersiveOnLoadWidgetId);
    }

    [Fact]
    public void KeepsAnImmersiveMarkOnASurvivingWidget()
    {
        var store = new MemoryConfigStore();
        var record = Record("y70-1", PanelSurfaces.Y70, "clock", InaType);
        record.Layout!.ImmersiveOnLoadWidgetId = record.Layout.Pages[0].Widgets[0].Id;
        store.Settings.PanelDevices["y70-1"] = record;
        var registry = new PanelDeviceRegistry(store);

        registry.RemoveWidgetType(InaType);

        Assert.Equal("w0", store.Settings.PanelDevices["y70-1"].Layout!.ImmersiveOnLoadWidgetId);
    }

    [Fact]
    public void AnEmptyTypeIsRefused()
    {
        var store = new MemoryConfigStore();
        store.Settings.PanelDevices["y70-1"] = Record("y70-1", PanelSurfaces.Y70, InaType);
        var registry = new PanelDeviceRegistry(store);

        Assert.Empty(registry.RemoveWidgetType(" "));
        Assert.Equal(0, store.Writes);
        Assert.Equal(new[] { InaType }, TypesOn(store, "y70-1"));
    }
}
