using System;
using Nexus.Service.Panel;
using Nexus.Service.Persistence;
using Xunit;

namespace Nexus.Service.Tests;

public class OverlaySupervisorTests
{
    private static readonly DateTime T0 = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    private static (OverlaySupervisor supervisor, FakeHost host, InMemoryConfigStore store) Build(bool y70 = false, bool streams = false)
    {
        var host = new FakeHost();
        var store = new InMemoryConfigStore();
        return (new OverlaySupervisor(host, store, () => y70, () => streams), host, store);
    }

    [Fact]
    public void Autolaunch_alone_does_not_start_the_host()
    {
        var (sup, host, _) = Build(y70: false);
        sup.Reconcile(T0);
        Assert.Equal(0, host.StartCalls);
    }

    [Fact]
    public void Autolaunch_with_a_detected_y70_starts_the_host_once()
    {
        var (sup, host, _) = Build(y70: true);
        sup.Reconcile(T0);
        sup.Reconcile(T0.AddSeconds(5));
        Assert.Equal(1, host.StartCalls);
    }

    [Fact]
    public void Shown_legacy_widgets_still_to_migrate_start_the_host()
    {
        var (sup, host, store) = Build();
        store.Update(s =>
        {
            s.Panel.AutoLaunch = false;
            s.Overlay.Enabled = true;
            s.Overlay.Layout = new() { new() };
        });
        sup.Reconcile(T0);
        Assert.Equal(1, host.StartCalls);
    }

    [Fact]
    public void Migrated_or_hidden_legacy_widgets_do_not_start_the_host()
    {
        var (sup, host, store) = Build();
        store.Update(s =>
        {
            s.Panel.AutoLaunch = false;
            s.Overlay.Enabled = false;
            s.Overlay.Layout = new() { new() };
        });
        sup.Reconcile(T0);
        store.Update(s =>
        {
            s.Overlay.Enabled = true;
            s.Overlay.MigratedToPanels = true;
        });
        sup.Reconcile(T0);
        Assert.Equal(0, host.StartCalls);
    }

    [Fact]
    public void Enabled_monitor_assignment_starts_the_host()
    {
        var (sup, host, store) = Build();
        store.Update(s => s.PanelDevices["m1"] = new Models.Panel.PanelDeviceRecord { Id = "m1", DisplayId = "d1" });
        sup.Reconcile(T0);
        Assert.Equal(1, host.StartCalls);
    }

    [Fact]
    public void Disabled_monitor_assignment_does_not()
    {
        var (sup, host, store) = Build();
        store.Update(s => s.PanelDevices["m1"] = new Models.Panel.PanelDeviceRecord { Id = "m1", DisplayId = "d1", Enabled = false });
        sup.Reconcile(T0);
        Assert.Equal(0, host.StartCalls);
    }

    [Fact]
    public void A_host_that_cannot_start_yet_is_retried_at_the_base_interval()
    {
        var (sup, host, _) = Build(y70: true);
        host.StartSucceeds = false;   // no console user yet
        var t = T0;
        for (var i = 0; i < 4; i++) { sup.Reconcile(t); t = t.AddSeconds(5); }
        Assert.Equal(4, host.StartCalls);
    }

    [Fact]
    public void A_host_that_starts_and_dies_backs_off()
    {
        var (sup, host, _) = Build(y70: true);
        sup.Reconcile(T0);
        host.Running = false;
        Assert.Equal(1, host.StartCalls);

        sup.Reconcile(T0.AddSeconds(4));
        Assert.Equal(1, host.StartCalls);
        sup.Reconcile(T0.AddSeconds(5));
        host.Running = false;
        Assert.Equal(2, host.StartCalls);

        sup.Reconcile(T0.AddSeconds(14));
        Assert.Equal(2, host.StartCalls);
        sup.Reconcile(T0.AddSeconds(15));
        Assert.Equal(3, host.StartCalls);
    }

    [Fact]
    public void Backoff_never_exceeds_five_minutes()
    {
        var (sup, host, _) = Build(y70: true);
        var t = T0;
        for (var i = 0; i < 12; i++) { sup.Reconcile(t); host.Running = false; t = t.AddMinutes(10); }
        Assert.Equal(12, host.StartCalls);

        // Doubling is long past the cap, so the next attempt is five minutes
        // after the last one, not longer.
        var last = t.AddMinutes(-10);
        sup.Reconcile(last.AddMinutes(4));
        Assert.Equal(12, host.StartCalls);
        sup.Reconcile(last.AddMinutes(5));
        Assert.Equal(13, host.StartCalls);
    }

    [Fact]
    public void Something_newly_wanting_a_host_clears_the_backoff()
    {
        var (sup, host, store) = Build(y70: false);
        store.Update(s => s.Panel.AutoLaunch = false);
        sup.Reconcile(T0);
        Assert.Equal(0, host.StartCalls);

        store.Update(s => s.PanelDevices["a"] = new() { Id = "a", DisplayId = "D1" });
        sup.Reconcile(T0.AddSeconds(1));
        Assert.Equal(1, host.StartCalls);
    }

    [Fact]
    public void Nothing_desired_never_starts()
    {
        var (sup, host, store) = Build();
        store.Update(s => s.Panel.AutoLaunch = false);
        sup.Reconcile(T0);
        Assert.Equal(0, host.StartCalls);
    }

    private sealed class FakeHost : IOverlayHost
    {
        public bool Running;
        public bool StartSucceeds = true;
        public int StartCalls;
        public bool IsRunning => Running;
        public bool Start() { StartCalls++; if (StartSucceeds) Running = true; return StartSucceeds; }
        public void Stop() { Running = false; }
        public void NotifyDisplayAssignmentsChanged() { }
    }

    private sealed class InMemoryConfigStore : IConfigStore
    {
        private readonly NexusSettings _settings = new();
        public string SettingsPath => ":memory:";
        public NexusSettings Load() => _settings;
        public void Update(Action<NexusSettings> mutator) { mutator(_settings); OnChanged?.Invoke(); }
        public void Reload() { }
        public void FlushNow() { }
        public event Action? OnChanged;
    }
}
