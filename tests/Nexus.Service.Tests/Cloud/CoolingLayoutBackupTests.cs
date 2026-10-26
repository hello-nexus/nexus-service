using System.Text;
using System.Text.Json;
using Nexus.Service.Cloud;
using Nexus.Service.Cooling;
using Nexus.Service.Models.Cloud;
using Nexus.Service.Persistence;
using Nexus.Service.Widgets;

namespace Nexus.Service.Tests.Cloud;

public sealed class CoolingLayoutBackupTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "nexus-cooling-backup-" + Guid.NewGuid().ToString("N")[..8]);
    private readonly CoolingLayoutStore _store;

    public CoolingLayoutBackupTests()
    {
        Directory.CreateDirectory(_dir);
        _store = new CoolingLayoutStore(_dir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* already gone */ }
    }

    private static CoolingLayout Layout(int directions = 2, int bindings = 1, string channel = "ch-1")
    {
        var layout = new CoolingLayout();
        for (var i = 0; i < directions; i++)
        {
            layout.Directions["slot-" + i] = i % 2 == 0 ? "exhaust" : "intake";
        }
        for (var i = 0; i < bindings; i++)
        {
            layout.Bindings.Add(new CoolingBinding { ChannelId = i == 0 ? channel : "channel-" + i, SlotKeys = ["slot-" + i, "slot-" + (i + 1)] });
        }
        return layout;
    }

    private static Dictionary<string, Dictionary<string, JsonElement>> BundleWith(CoolingLayout layout) => new()
    {
        [CoolingLayoutBackup.AppId] = new() { [CoolingLayoutBackup.Key] = JsonSerializer.SerializeToElement(layout, Nexus.Service.Serialization.PersistenceJsonContext.Default.CoolingLayout) },
    };

    [Fact]
    public void Attach_adds_the_layout_without_a_revision_and_digest_tracks_the_content()
    {
        _store.TrySave(0, Layout(), out _);

        var result = CoolingLayoutBackup.Attach(new(), _store, out var digest);

        var doc = result[CoolingLayoutBackup.AppId][CoolingLayoutBackup.Key];
        Assert.Equal("exhaust", doc.GetProperty("directions").GetProperty("slot-0").GetString());
        Assert.False(doc.TryGetProperty("rev", out _));
        Assert.Equal(digest, CoolingLayoutBackup.Digest(_store));

        _store.TrySave(1, Layout(directions: 3), out _);
        Assert.NotEqual(digest, CoolingLayoutBackup.Digest(_store));
    }

    [Fact]
    public void Without_a_saved_layout_nothing_is_attached_and_the_digest_is_null()
    {
        var input = new Dictionary<string, Dictionary<string, JsonElement>>();

        Assert.Same(input, CoolingLayoutBackup.Attach(input, _store, out var digest));
        Assert.Null(digest);
        Assert.Null(CoolingLayoutBackup.Digest(_store));
    }

    [Fact]
    public void Detach_strips_the_reserved_app_and_returns_a_valid_layout_even_with_unknown_channels()
    {
        var bundle = BundleWith(Layout(channel: "channel-this-machine-lacks"));
        bundle["com.test.app"] = new() { ["save"] = JsonSerializer.SerializeToElement(1) };

        var rest = CoolingLayoutBackup.Detach(bundle, out var layout);

        Assert.Equal(["com.test.app"], rest!.Keys);
        Assert.Equal("channel-this-machine-lacks", Assert.Single(layout!.Bindings).ChannelId);
    }

    [Fact]
    public void Detach_drops_an_invalid_layout_but_still_strips_the_reserved_app()
    {
        var bad = Layout();
        bad.Directions["slot-0"] = "sideways";

        var rest = CoolingLayoutBackup.Detach(BundleWith(bad), out var layout);

        Assert.Null(layout);
        Assert.Empty(rest!);
    }

    [Fact]
    public void Replace_saves_a_restored_layout_and_bumps_the_rev()
    {
        _store.TrySave(0, Layout(), out _);

        var saved = _store.Replace(Layout(directions: 5));

        Assert.Equal(2, saved.Rev);
        Assert.Equal(5, _store.Load().Layout.Directions.Count);
    }

    [Fact]
    public void Twelve_directions_and_six_bindings_back_up_in_under_2_KB()
    {
        _store.TrySave(0, Layout(directions: 12, bindings: 6), out _);

        var doc = CoolingLayoutBackup.Attach(new(), _store, out _)[CoolingLayoutBackup.AppId][CoolingLayoutBackup.Key];

        var bytes = Encoding.UTF8.GetByteCount(doc.GetRawText());
        Assert.InRange(bytes, 1, 2 * 1024 - 1);
    }
}
