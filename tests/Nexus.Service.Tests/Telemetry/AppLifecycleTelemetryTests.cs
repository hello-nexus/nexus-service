using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using Nexus.Service.Models.Panel;
using Nexus.Service.Models.Widgets;
using Nexus.Service.Store;
using Nexus.Service.Telemetry;
using Nexus.Service.Tests.Store;
using Nexus.Service.Widgets;
using Xunit;

namespace Nexus.Service.Tests.Telemetry;

public sealed class AppLifecycleTelemetryTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "app-lifecycle-" + Guid.NewGuid().ToString("N"));

    public AppLifecycleTelemetryTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    private sealed class Recorder : ITelemetry
    {
        public List<(string Event, Dictionary<string, object?> Props)> Events { get; } = new();
        public void Capture(string @event, params (string Key, object? Value)[] properties) =>
            Events.Add((@event, properties.ToDictionary(p => p.Key, p => p.Value)));
        public void Identify(params (string Key, object? Value)[] properties) { }
        public string[] Names => Events.Select(e => e.Event).ToArray();
    }

    private static PanelLayoutDto Layout(string surface, params (string Id, string Type)[] widgets)
    {
        var page = new PanelPageDto { Id = "p1" };
        foreach (var (id, type) in widgets)
            page.Widgets.Add(new PanelWidgetDto { Id = id, Type = type });
        return new PanelLayoutDto { Surface = surface, Pages = { page } };
    }

    [Fact]
    public void Adding_an_app_widget_emits_app_placed_with_the_surface()
    {
        var t = new Recorder();
        AppLifecycleTelemetry.LayoutChanged(t, Layout("y70", ("a", "clock")), Layout("y70", ("a", "clock"), ("b", "app:com.x.app")));

        var e = Assert.Single(t.Events);
        Assert.Equal(TelemetryEvents.AppPlaced, e.Event);
        Assert.Equal("com.x.app", e.Props["app_id"]);
        Assert.Equal("y70", e.Props["surface"]);
    }

    [Fact]
    public void Surface_override_names_the_desktop_dashboard_and_first_seen_still_emits_nothing()
    {
        var t = new Recorder();
        AppLifecycleTelemetry.LayoutChanged(t, Layout("y70"), Layout("y70", ("b", "app:com.x.app")), "desktop");
        AppLifecycleTelemetry.LayoutChanged(t, null, Layout("y70", ("c", "app:com.y.app")), "desktop");

        var e = Assert.Single(t.Events);
        Assert.Equal("desktop", e.Props["surface"]);
    }

    [Fact]
    public void Removing_an_app_widget_emits_app_removed()
    {
        var t = new Recorder();
        AppLifecycleTelemetry.LayoutChanged(t, Layout("y70", ("b", "app:com.x.app")), Layout("y70"));
        Assert.Equal(new[] { TelemetryEvents.AppRemoved }, t.Names);
    }

    [Fact]
    public void Resaving_the_same_widgets_or_moving_them_emits_nothing()
    {
        var t = new Recorder();
        var before = Layout("y70", ("b", "app:com.x.app"), ("c", "clock"));
        var after = Layout("y70", ("c", "clock"), ("b", "app:com.x.app"));
        after.Pages[0].Widgets.Single(w => w.Id == "b").Col = 3;
        AppLifecycleTelemetry.LayoutChanged(t, before, after);
        Assert.Empty(t.Events);
    }

    [Fact]
    public void A_layout_with_no_previous_one_emits_nothing_and_non_app_types_are_ignored()
    {
        var t = new Recorder();
        AppLifecycleTelemetry.LayoutChanged(t, null, Layout("y70", ("b", "app:com.x.app")));
        AppLifecycleTelemetry.LayoutChanged(t, Layout("y70"), Layout("y70", ("a", "clock"), ("d", "app:")));
        Assert.Empty(t.Events);
    }

    [Fact]
    public void A_second_instance_of_the_same_app_counts_as_a_placement()
    {
        var t = new Recorder();
        AppLifecycleTelemetry.LayoutChanged(t, Layout("y70", ("b", "app:com.x.app")), Layout("y70", ("b", "app:com.x.app"), ("c", "app:com.x.app")));
        Assert.Equal(new[] { TelemetryEvents.AppPlaced }, t.Names);
    }

    private static DateTime Day(int d) => new(2026, 10, d, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void App_used_fires_once_per_app_per_utc_day_and_again_the_next_day()
    {
        var t = new Recorder();
        var now = Day(21);
        var tracker = new AppUsageTracker(t, Path.Combine(_root, "u.json"), () => now);

        tracker.Note("com.x.app", "1.0.0");
        tracker.Note("com.x.app", "1.0.0");
        tracker.Note("com.y.app", "2.0.0");
        Assert.Equal(2, t.Events.Count);
        Assert.Equal("1.0.0", t.Events[0].Props["app_version"]);

        now = Day(22);
        tracker.Note("com.x.app", "1.0.0");
        Assert.Equal(3, t.Events.Count);
    }

    [Fact]
    public void App_used_dedupe_survives_a_restart_and_a_corrupt_file_is_ignored()
    {
        var path = Path.Combine(_root, "u.json");
        var t = new Recorder();
        new AppUsageTracker(t, path, () => Day(21)).Note("com.x.app", "1.0.0");
        new AppUsageTracker(t, path, () => Day(21)).Note("com.x.app", "1.0.0");
        Assert.Single(t.Events);

        File.WriteAllText(path, "{broken");
        new AppUsageTracker(t, path, () => Day(21)).Note("com.x.app", "1.0.0");
        Assert.Equal(2, t.Events.Count);
    }

    private sealed class ByteHandler(byte[] body) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(body) });
    }

    private static byte[] Artifact(string id, string version)
    {
        using var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
        {
            using (var w = new StreamWriter(zip.CreateEntry("manifest.json").Open()))
                w.Write($"{{\"schema\":\"nexus.app/1\",\"id\":\"{id}\",\"version\":\"{version}\",\"name\":\"T\",\"capabilities\":{{}}}}");
            using (var w = new StreamWriter(zip.CreateEntry("widget.mjs").Open()))
                w.Write("export default 1;");
        }
        return ms.ToArray();
    }

    private async Task<StoreInstallResponse> Install(Recorder t, string version, bool update = false)
    {
        const string id = "com.hellonexus.aquarium";
        var bytes = Artifact(id, version);
        var sha = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        var installer = new StoreInstaller(new HttpClient(new ByteHandler(bytes)), new AppRegistry(), () => _root, StoreTestSigning.PublicKeySpki, t);
        var req = new StoreInstallRequest { AppId = id, Version = version, Sha256 = sha, Signature = StoreTestSigning.Sign(id, version, sha) };
        return update ? await installer.UpdateAsync(req, default) : await installer.InstallAsync(req, default);
    }

    [Fact]
    public async Task Store_install_then_update_emit_installed_then_updated_with_from_version_and_reason()
    {
        var t = new Recorder();
        Assert.True((await Install(t, "1.0.0")).Ok);
        Assert.True((await Install(t, "1.1.0", update: true)).Ok);

        Assert.Equal(new[] { TelemetryEvents.AppInstalled, TelemetryEvents.AppUpdated }, t.Names);
        Assert.Equal("user", t.Events[0].Props["reason"]);
        Assert.Equal("1.0.0", t.Events[1].Props["from_version"]);
        Assert.Equal("1.1.0", t.Events[1].Props["app_version"]);
        Assert.Equal("auto", t.Events[1].Props["reason"]);
    }

    [Fact]
    public async Task Reinstalling_the_same_version_and_failures_emit_nothing()
    {
        var t = new Recorder();
        await Install(t, "1.0.0");
        t.Events.Clear();
        await Install(t, "1.0.0");
        Assert.Empty(t.Events);

        var other = new StoreInstaller(new HttpClient(new ByteHandler(Array.Empty<byte>())), new AppRegistry(), () => _root, StoreTestSigning.PublicKeySpki, t);
        var bad = await other.InstallAsync(new StoreInstallRequest { AppId = "com.hellonexus.aquarium", Version = "3.0.0", Sha256 = "00", Signature = "x" }, default);
        Assert.False(bad.Ok);
        Assert.Empty(t.Events);
    }

    [Theory]
    [InlineData("user")]
    [InlineData("yank")]
    public void Uninstall_emits_app_uninstalled_with_the_removed_version_and_reason(string reason)
    {
        var dir = Path.Combine(_root, "com.x.app");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "manifest.json"), "{\"version\":\"4.2.0\"}");
        var t = new Recorder();
        var installer = new AppInstaller(new AppRegistry(() => new List<AppInstallPaths.Root>()), new Nexus.Service.Sensors.OemInfo(), t, () => _root);

        Assert.Null(installer.Uninstall("com.x.app", reason).Error);
        installer.Uninstall("com.x.app", reason);

        var e = Assert.Single(t.Events);
        Assert.Equal(TelemetryEvents.AppUninstalled, e.Event);
        Assert.Equal("4.2.0", e.Props["app_version"]);
        Assert.Equal(reason, e.Props["reason"]);
    }
}
