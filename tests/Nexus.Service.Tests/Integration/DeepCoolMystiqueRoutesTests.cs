using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Nexus.Service.Auth;
using Nexus.Service.Devices;
using Nexus.Service.Peripherals.DeepCool;
using Nexus.Service.Persistence;
using Nexus.Service.Routes;
using Xunit;

namespace Nexus.Service.Tests.Integration;

public partial class DeepCoolMystiqueRoutesTests
{
    private static HttpClient Client(NexusAppFactory factory)
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", factory.Services.GetRequiredService<TokenService>().Token);
        return client;
    }

    private static StringContent Json(string body) => new(body, Encoding.UTF8, "application/json");

    [Fact]
    public async Task State_starts_with_no_mode_chosen_and_follows_a_saved_change()
    {
        using var factory = new NexusAppFactory();
        var client = Client(factory);

        using (var first = JsonDocument.Parse(await client.GetStringAsync("/devices/deepcool-mystique/state")))
        {
            Assert.False(first.RootElement.GetProperty("modeChosen").GetBoolean());
            Assert.False(first.RootElement.GetProperty("readoutChosen").GetBoolean());
            Assert.False(first.RootElement.GetProperty("recordingChosen").GetBoolean());
            Assert.False(first.RootElement.GetProperty("deviceChosen").GetBoolean());
            Assert.False(first.RootElement.GetProperty("isConnected").GetBoolean());
        }

        var put = await client.PutAsync("/devices/deepcool-mystique/settings",
            Json("{\"mode\":\"recording\",\"brightness\":40,\"recordTemperature\":false}"));
        Assert.Equal(HttpStatusCode.OK, put.StatusCode);

        using var after = JsonDocument.Parse(await client.GetStringAsync("/devices/deepcool-mystique/state"));
        Assert.True(after.RootElement.GetProperty("modeChosen").GetBoolean());
        Assert.True(after.RootElement.GetProperty("deviceChosen").GetBoolean());
        Assert.True(after.RootElement.GetProperty("recordingChosen").GetBoolean());
        Assert.False(after.RootElement.GetProperty("readoutChosen").GetBoolean());
        Assert.Equal("recording", after.RootElement.GetProperty("mode").GetString());
        Assert.Equal(40, after.RootElement.GetProperty("brightness").GetInt32());
        Assert.False(after.RootElement.GetProperty("recordTemperature").GetBoolean());
    }

    [Theory]
    [InlineData("{\"mode\":\"gaming\"}")]
    [InlineData("{\"readout\":\"voltage\"}")]
    [InlineData("{\"style\":\"gauge\"}")]
    [InlineData("{\"auxArea\":\"fps\"}")]
    [InlineData("{\"switchSeconds\":4}")]
    [InlineData("{\"animation\":\"wipe\"}")]
    [InlineData("{\"mediaSource\":\"video\"}")]
    [InlineData("{\"cpuFan\":{\"category\":\"nonsense\",\"sensor\":\"x\"}}")]
    [InlineData("{\"pump\":{\"category\":\"\",\"sensor\":\"orphan\"}}")]
    public async Task Unknown_values_are_refused(string body)
    {
        using var factory = new NexusAppFactory();
        var client = Client(factory);

        var put = await client.PutAsync("/devices/deepcool-mystique/settings", Json(body));

        Assert.Equal(HttpStatusCode.BadRequest, put.StatusCode);
    }

    [Fact]
    public async Task A_sensor_that_does_not_exist_is_refused_and_an_empty_pick_returns_to_automatic()
    {
        using var factory = new NexusAppFactory();
        var client = Client(factory);

        var missing = await client.PutAsync("/devices/deepcool-mystique/settings",
            Json("{\"cpuFan\":{\"category\":\"motherboard\",\"sensor\":\"fan/none\"}}"));
        Assert.Equal(HttpStatusCode.BadRequest, missing.StatusCode);

        var auto = await client.PutAsync("/devices/deepcool-mystique/settings",
            Json("{\"cpuFan\":{\"category\":\"\",\"sensor\":\"\"}}"));
        Assert.Equal(HttpStatusCode.OK, auto.StatusCode);
    }

    [Fact]
    public async Task An_unrelated_change_succeeds_while_the_stored_pick_is_absent_from_this_pc()
    {
        using var factory = new NexusAppFactory();
        factory.Services.GetRequiredService<IConfigStore>().Update(
            s => s.Devices.DeepCoolMystique.CpuFan = new DeepCoolMystiqueSensorPick { Category = "motherboard", Sensor = "fan/unplugged" });
        var client = Client(factory);

        var put = await client.PutAsync("/devices/deepcool-mystique/settings",
            Json("{\"brightness\":50,\"cpuFan\":{\"category\":\"motherboard\",\"sensor\":\"fan/unplugged\"}}"));

        Assert.Equal(HttpStatusCode.OK, put.StatusCode);
    }

    [Theory]
    [InlineData("cpuTemperature", "{\"category\":\"cpu\",\"sensor\":\"fan/x\"}")]
    [InlineData("ramUsage", "{\"category\":\"nonsense\",\"sensor\":\"x\"}")]
    [InlineData("cpuFrequency", "{\"category\":\"\",\"sensor\":\"orphan\"}")]
    public async Task A_new_readout_pick_must_be_a_real_sensor_of_its_type(string field, string pick)
    {
        using var factory = new NexusAppFactory();
        var client = Client(factory);

        var put = await client.PutAsync("/devices/deepcool-mystique/settings", Json($"{{\"{field}\":{pick}}}"));

        Assert.Equal(HttpStatusCode.BadRequest, put.StatusCode);
    }

    [Fact]
    public async Task Every_readout_can_return_to_automatic()
    {
        using var factory = new NexusAppFactory();
        var client = Client(factory);

        var put = await client.PutAsync("/devices/deepcool-mystique/settings", Json(
            "{\"cpuTemperature\":{\"category\":\"\",\"sensor\":\"\"},\"cpuFrequency\":{\"category\":\"\",\"sensor\":\"\"},"
            + "\"cpuUsage\":{\"category\":\"\",\"sensor\":\"\"},\"ramUsage\":{\"category\":\"\",\"sensor\":\"\"}}"));

        Assert.Equal(HttpStatusCode.OK, put.StatusCode);
        using var state = JsonDocument.Parse(await client.GetStringAsync("/devices/deepcool-mystique/state"));
        Assert.True(state.RootElement.TryGetProperty("ramUsage", out _));
        Assert.True(state.RootElement.TryGetProperty("cpuFrequency", out _));
    }

    [Fact]
    public void Each_setting_marks_only_its_own_command_group_as_edited()
    {
        var m = new DeepCoolMystiqueSettings();

        DevicesRoutes.ApplyMystiqueRequest(m, new DeepCoolMystiqueSettingsRequest { Brightness = 300 });
        Assert.True(m.DeviceEdited);
        Assert.Equal(100, m.Brightness);
        Assert.False(m.ModeEdited || m.ReadoutEdited || m.RecordingEdited);

        DevicesRoutes.ApplyMystiqueRequest(m, new DeepCoolMystiqueSettingsRequest { Readout = DeepCoolMystiqueSettings.ReadoutFanPump });
        Assert.True(m.ReadoutEdited);
        Assert.False(m.ModeEdited || m.RecordingEdited);

        DevicesRoutes.ApplyMystiqueRequest(m, new DeepCoolMystiqueSettingsRequest { RecordFrequency = false });
        Assert.True(m.RecordingEdited);
        Assert.False(m.RecordFrequency);
        Assert.False(m.ModeEdited);
    }

    [Fact]
    public void Media_and_style_settings_mark_the_group_of_the_command_that_carries_them()
    {
        var m = new DeepCoolMystiqueSettings();

        DevicesRoutes.ApplyMystiqueRequest(m, new DeepCoolMystiqueSettingsRequest { Style = DeepCoolMystiqueSettings.StyleRing, AuxArea = DeepCoolMystiqueSettings.AuxVoltage });
        Assert.True(m.ReadoutEdited);
        Assert.False(m.MediaEdited || m.SourceEdited || m.DeviceEdited);

        DevicesRoutes.ApplyMystiqueRequest(m, new DeepCoolMystiqueSettingsRequest { SwitchSeconds = 7, Animation = DeepCoolMystiqueSettings.AnimationFade });
        Assert.True(m.MediaEdited);
        Assert.False(m.SourceEdited || m.DeviceEdited);

        DevicesRoutes.ApplyMystiqueRequest(m, new DeepCoolMystiqueSettingsRequest { MediaSource = DeepCoolMystiqueSettings.SourceGif });
        Assert.True(m.SourceEdited);
        Assert.False(m.DeviceEdited);

        DevicesRoutes.ApplyMystiqueRequest(m, new DeepCoolMystiqueSettingsRequest { EdgePicking = true });
        Assert.True(m.DeviceEdited && m.EdgePicking);
    }

    [Theory]
    [InlineData("picture", MystiqueMedia.KindPicture, MystiqueMedia.MaxPictures)]
    [InlineData("gif", MystiqueMedia.KindGif, MystiqueMedia.MaxGifs)]
    public void Uploads_take_a_new_slot_until_the_kind_is_full_then_only_replace(string kind, byte wireKind, int max)
    {
        using var factory = new NexusAppFactory();
        var store = factory.Services.GetRequiredService<IConfigStore>();
        var held = new List<DeepCoolMystiqueMediaItem>();
        for (byte i = 0; i < max; i++)
        {
            held.Add(new DeepCoolMystiqueMediaItem { Kind = kind, Id = MystiqueMedia.IdOf(wireKind, new[] { i }) });
        }
        var other = kind == "gif" ? "picture" : "gif";
        held.Add(new DeepCoolMystiqueMediaItem { Kind = other, Id = "acd9325521f3493c86bfc0b0aa85658d" });
        store.Update(s => s.Devices.DeepCoolMystique.Media = held.ConvertAll(i => i.Copy()));
        var fresh = new byte[] { 200 };

        Assert.Null(DevicesRoutes.MystiqueUploadId(store, kind, fresh, ""));
        Assert.Null(DevicesRoutes.MystiqueUploadId(store, kind, fresh, "acd9325521f3493c86bfc0b0aa85658d"));
        Assert.Equal((held[1].Id, held[1].Id), DevicesRoutes.MystiqueUploadId(store, kind, fresh, held[1].Id));
        Assert.Equal((held[0].Id, held[0].Id), DevicesRoutes.MystiqueUploadId(store, kind, new byte[] { 0 }, ""));

        store.Update(s => s.Devices.DeepCoolMystique.Media.RemoveAt(max - 1));
        var id = MystiqueMedia.IdOf(wireKind, fresh);
        Assert.Equal((id, id), DevicesRoutes.MystiqueUploadId(store, kind, fresh, ""));
    }

    [Fact]
    public async Task Removing_a_picture_frees_its_id_and_removing_the_shown_gif_leaves_none_shown()
    {
        using var factory = new NexusAppFactory();
        var client = Client(factory);
        var store = factory.Services.GetRequiredService<IConfigStore>();
        const string pictureId = "2101b6e6277841daa821e8d79a5c528b";
        const string gifId = "acd9325521f3493c86bfc0b0aa85658d";
        store.Update(s => s.Devices.DeepCoolMystique.Media = new()
        {
            new DeepCoolMystiqueMediaItem { Kind = "picture", Id = pictureId },
            new DeepCoolMystiqueMediaItem { Kind = "gif", Id = gifId },
        });
        store.Update(s => (s.Devices.DeepCoolMystique.GifSlot, s.Devices.DeepCoolMystique.ShownGif) = (gifId, gifId));

        Assert.Equal(HttpStatusCode.OK, (await client.DeleteAsync($"/devices/deepcool-mystique/media/{pictureId}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.DeleteAsync($"/devices/deepcool-mystique/media/{pictureId}")).StatusCode);
        Assert.Equal((MystiqueMedia.IdOf(MystiqueMedia.KindPicture, new byte[] { 9 }), pictureId), DevicesRoutes.MystiqueUploadId(store, "picture", new byte[] { 9 }, ""));
        Assert.NotEqual(pictureId, DevicesRoutes.MystiqueUploadId(store, "gif", new byte[] { 9 }, "")!.Value.ScreenId);

        Assert.Equal(HttpStatusCode.OK, (await client.DeleteAsync($"/devices/deepcool-mystique/media/{gifId}")).StatusCode);
        var m = new DeepCoolMystiqueSettings();
        store.Read(s => m = s.Devices.DeepCoolMystique.Copy());
        Assert.Empty(m.Media);
        Assert.Equal(new[] { pictureId }, m.Freed.ConvertAll(i => i.Id));
        Assert.Equal((gifId, ""), (m.GifSlot, m.ShownGif));
        var gif = MystiqueMedia.IdOf(MystiqueMedia.KindGif, new byte[] { 9 });
        Assert.Equal((gif, gif), DevicesRoutes.MystiqueUploadId(store, "gif", new byte[] { 9 }, ""));
    }

    [Fact]
    public void A_swapped_picture_is_found_and_freed_under_its_screen_id_and_a_source_that_collides_takes_a_new_one()
    {
        using var factory = new NexusAppFactory();
        var store = factory.Services.GetRequiredService<IConfigStore>();
        var source = new byte[] { 9 };
        string sourceId = MystiqueMedia.IdOf(MystiqueMedia.KindPicture, source);
        const string other = "2101b6e6277841daa821e8d79a5c528b";
        store.Update(s => s.Devices.DeepCoolMystique.Media = new()
        {
            new DeepCoolMystiqueMediaItem { Kind = "picture", Id = other, ScreenId = sourceId },
            new DeepCoolMystiqueMediaItem { Kind = "picture", Id = sourceId, ScreenId = other },
        });

        Assert.Equal((sourceId, other), DevicesRoutes.MystiqueUploadId(store, "picture", source, ""));
        Assert.Equal((other, sourceId), DevicesRoutes.MystiqueUploadId(store, "picture", new byte[] { 1 }, other));

        store.Update(s => s.Devices.DeepCoolMystique.Media.RemoveAt(1));
        var fresh = DevicesRoutes.MystiqueUploadId(store, "picture", source, "")!.Value;
        Assert.Equal(sourceId, fresh.Id);
        Assert.True(MystiqueMedia.IsId(fresh.ScreenId) && fresh.ScreenId != sourceId);
    }

    [Fact]
    public async Task Removing_a_swapped_picture_frees_the_id_the_screen_holds_it_under()
    {
        using var factory = new NexusAppFactory();
        var client = Client(factory);
        var store = factory.Services.GetRequiredService<IConfigStore>();
        const string first = "2101b6e6277841daa821e8d79a5c528b";
        const string second = "acd9325521f3493c86bfc0b0aa85658d";
        store.Update(s => s.Devices.DeepCoolMystique.Media = new()
        {
            new DeepCoolMystiqueMediaItem { Kind = "picture", Id = first, ScreenId = second },
            new DeepCoolMystiqueMediaItem { Kind = "picture", Id = second, ScreenId = first },
        });
        store.Update(s => s.Devices.DeepCoolMystique.PictureIds = new() { second, first });

        Assert.Equal(HttpStatusCode.OK, (await client.DeleteAsync($"/devices/deepcool-mystique/media/{second}")).StatusCode);

        var m = new DeepCoolMystiqueSettings();
        store.Read(s => m = s.Devices.DeepCoolMystique.Copy());
        Assert.Equal(new[] { first }, m.Freed.ConvertAll(i => i.Id));
        Assert.Equal(new[] { second, first }, m.PictureIds);
        Assert.Equal(first, DevicesRoutes.MystiquePictureSlot(m));
        Assert.Equal(second, DevicesRoutes.MystiqueScreenId(m.Media.Single()));
    }

    private const string Third = "83182dc7830844c89ca5f01e57f63ad9";

    [Fact]
    public void Without_a_recorded_order_pictures_keep_list_order_under_their_own_id_while_the_screen_holds_it()
    {
        var m = new DeepCoolMystiqueSettings();
        Assert.Null(DevicesRoutes.MystiquePictureSlot(m));
        m.Freed.Add(new DeepCoolMystiqueMediaItem { Kind = "picture", Id = Third });
        Assert.Equal(Third, DevicesRoutes.MystiquePictureSlot(m));

        // Pictures added in order, then swapped.
        m.Freed.Clear();
        m.Media.Add(new DeepCoolMystiqueMediaItem { Kind = "picture", Id = Clicked });
        m.Media.Add(new DeepCoolMystiqueMediaItem { Kind = "gif", Id = Other });
        m.Media.Add(new DeepCoolMystiqueMediaItem { Kind = "picture", Id = First, ScreenId = Third });
        m.Media.Add(new DeepCoolMystiqueMediaItem { Kind = "picture", Id = Third, ScreenId = First });

        Assert.Equal(new[] { Clicked, First, Third }, DevicesRoutes.MystiquePictureOrder(m));
        Assert.Equal(Third, DevicesRoutes.MystiqueShownPicture(m));

        // A picture that went up under a reused id adds no place; the id it holds goes first.
        m.Media[3].Id = Other;
        Assert.Equal(new[] { Third, Clicked, First }, DevicesRoutes.MystiquePictureOrder(m));
    }

    [Fact]
    public void A_recorded_order_wins_and_keeps_only_recorded_ids_the_screen_holds()
    {
        var m = new DeepCoolMystiqueSettings { PictureIds = new() { Other, First, Clicked } };
        m.Media.Add(new DeepCoolMystiqueMediaItem { Kind = "picture", Id = Clicked });
        m.Freed.Add(new DeepCoolMystiqueMediaItem { Kind = "picture", Id = First });
        m.Freed.Add(new DeepCoolMystiqueMediaItem { Kind = "picture", Id = Third });

        Assert.Equal(new[] { First, Clicked }, DevicesRoutes.MystiquePictureOrder(m));
        Assert.Equal(First, DevicesRoutes.MystiquePictureSlot(m));
    }

    [Fact]
    public void Only_an_id_the_screen_did_not_hold_is_added_to_the_order()
    {
        var m = new DeepCoolMystiqueSettings();
        m.Media.Add(new DeepCoolMystiqueMediaItem { Kind = "picture", Id = Clicked });
        m.Freed.Add(new DeepCoolMystiqueMediaItem { Kind = "picture", Id = First });

        DevicesRoutes.NoteMystiquePictureStored(m, Clicked);
        DevicesRoutes.NoteMystiquePictureStored(m, First);
        Assert.Equal(new[] { First, Clicked }, m.PictureIds);

        DevicesRoutes.NoteMystiquePictureStored(m, Third);
        m.Media.Add(new DeepCoolMystiqueMediaItem { Kind = "picture", Id = Third });
        Assert.Equal(new[] { First, Clicked, Third }, m.PictureIds);
        Assert.Equal(First, DevicesRoutes.MystiquePictureSlot(m));
    }

    [Theory]
    [InlineData("", "d0000000000000000000000000000001", "20000000000000000000000000000002", "f0000000000000000000000000000003",
        "d0000000000000000000000000000001", "20000000000000000000000000000002", "20000000000000000000000000000002")]
    [InlineData("", "90000000000000000000000000000001", "b0000000000000000000000000000002", "20000000000000000000000000000003",
        "90000000000000000000000000000001", "90000000000000000000000000000001", "20000000000000000000000000000003")]
    [InlineData("10000000000000000000000000000004", "d0000000000000000000000000000001", "20000000000000000000000000000002", "f0000000000000000000000000000003",
        "10000000000000000000000000000004", "10000000000000000000000000000004", "10000000000000000000000000000004")]
    public void A_new_picture_and_each_click_show_from_the_lowest_stored_id(string freed, string p1, string p2, string p3, string slot1, string slot2, string slot3)
    {
        var m = new DeepCoolMystiqueSettings();
        if (freed != "")
        {
            m.Freed.Add(new DeepCoolMystiqueMediaItem { Kind = "picture", Id = freed });
        }
        void Upload(string id, string slot)
        {
            bool swaps = DevicesRoutes.MystiqueUploadSwaps(m, id);
            DevicesRoutes.NoteMystiquePictureStored(m, id);
            m.Media.Add(new DeepCoolMystiqueMediaItem { Kind = "picture", Id = id });
            Assert.Equal(id != slot, swaps);
            if (swaps)
            {
                Assert.True(Show(m, id, int.MaxValue) > 0);
            }
            Assert.Equal(slot, DevicesRoutes.MystiquePictureSlot(m));
            Assert.Equal(id, DevicesRoutes.MystiqueShownPicture(m));
        }
        void Click(string id, string slot)
        {
            Show(m, id, int.MaxValue);
            Assert.Equal(slot, DevicesRoutes.MystiquePictureSlot(m));
            Assert.Equal(id, DevicesRoutes.MystiqueShownPicture(m));
            Assert.Equal(0, Show(m, id, int.MaxValue));
        }

        Upload(p1, slot1);
        Upload(p2, slot2);
        foreach (var clicked in new[] { p1, p2, p1 })
        {
            Click(clicked, slot2);
        }
        Upload(p3, slot3);
        foreach (var clicked in new[] { p1, p3, p2, p3 })
        {
            Click(clicked, slot3);
        }
        Assert.True(m.Media.TrueForAll(i => !i.Unsent));
    }

    [Fact]
    public void Only_a_new_picture_above_the_lowest_held_picture_id_is_swapped()
    {
        var m = new DeepCoolMystiqueSettings();
        Assert.False(DevicesRoutes.MystiqueUploadSwaps(m, Clicked));

        m.Media.Add(new DeepCoolMystiqueMediaItem { Kind = "picture", Id = Clicked });
        m.Media.Add(new DeepCoolMystiqueMediaItem { Kind = "gif", Id = "00000000000000000000000000000001" });
        Assert.False(DevicesRoutes.MystiqueUploadSwaps(m, First));
        Assert.True(DevicesRoutes.MystiqueUploadSwaps(m, "f0000000000000000000000000000003"));

        m.Freed.Add(new DeepCoolMystiqueMediaItem { Kind = "picture", Id = "10000000000000000000000000000004" });
        Assert.True(DevicesRoutes.MystiqueUploadSwaps(m, First));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public void A_failed_swap_after_an_upload_leaves_the_broken_copy_unsent_and_the_next_show_repairs_it(int streamed)
    {
        var m = new DeepCoolMystiqueSettings();
        DevicesRoutes.NoteMystiquePictureStored(m, First);
        m.Media.Add(new DeepCoolMystiqueMediaItem { Kind = "picture", Id = First });
        Assert.True(DevicesRoutes.MystiqueUploadSwaps(m, Clicked));
        DevicesRoutes.NoteMystiquePictureStored(m, Clicked);
        m.Media.Add(new DeepCoolMystiqueMediaItem { Kind = "picture", Id = Clicked });

        Show(m, Clicked, streamed);

        Assert.Equal(streamed == 0
            ? new[] { (First, First, true), (Clicked, Clicked, false) }
            : new[] { (First, Clicked, true), (Clicked, First, false) }, Held(m));
        Assert.Equal(streamed == 0 ? "" : Clicked, DevicesRoutes.MystiqueShownPicture(m));
        Assert.Empty(m.Freed);

        Show(m, Clicked, int.MaxValue);
        Assert.Equal(new[] { (First, Clicked, false), (Clicked, First, false) }, Held(m));
        Assert.Equal(Clicked, DevicesRoutes.MystiqueShownPicture(m));
    }

    private const string Clicked = "acd9325521f3493c86bfc0b0aa85658d";
    private const string First = "2101b6e6277841daa821e8d79a5c528b";
    private const string Other = "41c9bf307cba45a1aa3a1d92bbe93b96";

    private static DeepCoolMystiqueSettings Pictures(bool displacedListed)
    {
        var m = new DeepCoolMystiqueSettings();
        m.Media.Add(new DeepCoolMystiqueMediaItem { Kind = "picture", Id = Clicked });
        if (displacedListed)
        {
            m.Media.Add(new DeepCoolMystiqueMediaItem { Kind = "picture", Id = First });
        }
        else
        {
            m.Freed.Add(new DeepCoolMystiqueMediaItem { Kind = "picture", Id = First });
        }
        return m;
    }

    private static (string, string, bool)[] Held(DeepCoolMystiqueSettings m) =>
        m.Media.ConvertAll(i => (i.Id, DevicesRoutes.MystiqueScreenId(i), i.Unsent)).ToArray();

    [Fact]
    public void A_whole_swap_moves_both_pictures()
    {
        var m = Pictures(displacedListed: true);

        DevicesRoutes.RecordMystiquePictureStreams(m, new[] { (Clicked, First), (First, Clicked) }, 2);

        Assert.Equal(new[] { (Clicked, First, false), (First, Clicked, false) }, Held(m));
        Assert.Empty(m.Freed);
    }

    [Fact]
    public void A_swap_whose_second_stream_failed_leaves_the_displaced_picture_unsent_until_it_streams()
    {
        var m = Pictures(displacedListed: true);

        DevicesRoutes.RecordMystiquePictureStreams(m, new[] { (Clicked, First), (First, Clicked) }, 1);

        Assert.Equal(new[] { (Clicked, First, false), (First, Clicked, true) }, Held(m));
        Assert.Empty(m.Freed);
        Assert.Equal(Clicked, DevicesRoutes.MystiqueShownPicture(m));

        DevicesRoutes.RecordMystiquePictureStreams(m, new[] { (First, Clicked) }, 1);

        Assert.Equal(new[] { (Clicked, First, false), (First, Clicked, false) }, Held(m));
    }

    [Fact]
    public void A_swap_whose_first_stream_failed_keeps_the_clicked_picture_and_leaves_the_first_unsent()
    {
        var m = Pictures(displacedListed: true);
        m.PictureIds = new() { Clicked, First };

        DevicesRoutes.RecordMystiquePictureStreams(m, new[] { (Clicked, First), (First, Clicked) }, 0);

        Assert.Equal(new[] { (Clicked, Clicked, false), (First, First, true) }, Held(m));
        Assert.Empty(m.Freed);
        Assert.Equal("", DevicesRoutes.MystiqueShownPicture(m));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(0)]
    public void A_move_into_a_freed_first_place_frees_the_old_id_only_once_it_lands(int streamed)
    {
        var m = Pictures(displacedListed: false);

        DevicesRoutes.RecordMystiquePictureStreams(m, new[] { (Clicked, First) }, streamed);

        Assert.Equal(new[] { (Clicked, streamed == 1 ? First : Clicked, false) }, Held(m));
        Assert.Equal(new[] { streamed == 1 ? Clicked : First }, m.Freed.ConvertAll(i => i.Id));
    }

    /// <summary>Records a show of <paramref name="clicked"/> whose first <paramref name="streamed"/> streams landed; returns how many it planned.</summary>
    private static int Show(DeepCoolMystiqueSettings m, string clicked, int streamed)
    {
        string slot = DevicesRoutes.MystiquePictureSlot(m)!;
        var plan = DevicesRoutes.MystiquePicturePlan(m, m.Media.Find(i => i.Id == clicked)!, slot);
        DevicesRoutes.RecordMystiquePictureStreams(m, plan.ConvertAll(p => (p.Item.Id, p.ScreenId)), Math.Min(streamed, plan.Count));
        return plan.Count;
    }

    [Fact]
    public void Two_failed_shows_in_a_row_keep_each_picture_under_its_own_id_and_the_next_whole_show_repairs_both()
    {
        var m = Pictures(displacedListed: true);
        m.PictureIds = new() { Clicked, First };

        Show(m, Clicked, 1);
        Assert.Equal(new[] { (Clicked, First, false), (First, Clicked, true) }, Held(m));
        Show(m, First, 0);
        Assert.Equal(new[] { (Clicked, First, true), (First, Clicked, true) }, Held(m));
        Assert.Empty(m.Freed);

        Show(m, First, int.MaxValue);
        Assert.Equal(new[] { (Clicked, Clicked, false), (First, First, false) }, Held(m));
        Assert.Equal(First, DevicesRoutes.MystiqueShownPicture(m));
    }

    [Fact]
    public void No_two_pictures_share_a_screen_id_after_any_one_or_two_shows_and_a_whole_show_leaves_none_unsent()
    {
        var ids = new[] { Clicked, First, Other };
        var start = new DeepCoolMystiqueSettings { PictureIds = new() { Clicked, Other, First } };
        foreach (var id in ids)
        {
            start.Media.Add(new DeepCoolMystiqueMediaItem { Kind = "picture", Id = id });
        }
        void AssertDistinct(DeepCoolMystiqueSettings m, string path)
        {
            var held = m.Media.ConvertAll(DevicesRoutes.MystiqueScreenId);
            Assert.True(held.Distinct().Count() == held.Count, path);
            Assert.DoesNotContain(m.Freed, f => held.Contains(f.Id));
        }
        foreach (var first in ids)
        {
            for (int s1 = 0; s1 <= 3; s1++)
            {
                var once = start.Copy();
                Show(once, first, s1);
                AssertDistinct(once, $"{first}/{s1}");
                foreach (var second in ids)
                {
                    for (int s2 = 0; s2 <= 3; s2++)
                    {
                        var twice = once.Copy();
                        int planned = Show(twice, second, s2);
                        string path = $"{first}/{s1} then {second}/{s2}";
                        AssertDistinct(twice, path);
                        if (s2 >= planned)
                        {
                            Assert.True(twice.Media.TrueForAll(i => !i.Unsent), path);
                            Assert.Equal(second, DevicesRoutes.MystiqueShownPicture(twice));
                        }
                    }
                }
            }
        }
    }

    [Fact]
    public void A_resend_that_fails_keeps_its_picture_unsent_and_leaves_the_rest_alone()
    {
        var m = Pictures(displacedListed: true);
        m.Media.Add(new DeepCoolMystiqueMediaItem { Kind = "picture", Id = Other, Unsent = true });
        m.Media.Add(new DeepCoolMystiqueMediaItem { Kind = "gif", Id = Other });

        DevicesRoutes.RecordMystiquePictureStreams(m, new[] { (Clicked, First), (First, Clicked), (Other, Other) }, 2);

        Assert.Equal(new[] { (Clicked, First, false), (First, Clicked, false), (Other, Other, true), (Other, Other, false) }, Held(m));
        Assert.Empty(m.Freed);
    }

    [Fact]
    public void Every_gif_goes_up_under_one_slot_taken_from_a_held_gif_until_one_is_recorded()
    {
        const string listed = "acd9325521f3493c86bfc0b0aa85658d";
        const string freed = "83182dc7830844c89ca5f01e57f63ad9";
        const string fresh = "41c9bf307cba45a1aa3a1d92bbe93b96";
        var m = new DeepCoolMystiqueSettings();

        Assert.True(m.GifSlotUnsure);
        Assert.Equal((fresh, false), DevicesRoutes.MystiqueGifSlot(m, fresh));

        m.Media = new()
        {
            new DeepCoolMystiqueMediaItem { Kind = "picture", Id = "2101b6e6277841daa821e8d79a5c528b" },
            new DeepCoolMystiqueMediaItem { Kind = "gif", Id = listed },
        };
        Assert.Equal((listed, true), DevicesRoutes.MystiqueGifSlot(m, fresh));

        m.Freed = new()
        {
            new DeepCoolMystiqueMediaItem { Kind = "picture", Id = "90b8cf628a5440908e6ad8ce51452ef8" },
            new DeepCoolMystiqueMediaItem { Kind = "gif", Id = freed },
        };
        Assert.Equal((freed, true), DevicesRoutes.MystiqueGifSlot(m, fresh));

        DevicesRoutes.SetMystiqueGifSlot(m, freed, listed);
        Assert.Equal((freed, listed), (m.GifSlot, m.ShownGif));
        Assert.Equal(new[] { "picture" }, m.Freed.ConvertAll(i => i.Kind));
        Assert.Equal((freed, true), DevicesRoutes.MystiqueGifSlot(m, fresh));
    }

    [Fact]
    public async Task State_reports_an_unsure_gif_slot_until_a_format()
    {
        using var factory = new NexusAppFactory();
        var client = Client(factory);
        var store = factory.Services.GetRequiredService<IConfigStore>();

        using (var unsure = JsonDocument.Parse(await client.GetStringAsync("/devices/deepcool-mystique/state")))
        {
            Assert.True(unsure.RootElement.GetProperty("gifSlotUnsure").GetBoolean());
        }
        var saved = System.Text.Json.JsonSerializer.Deserialize("{\"devices\":{\"deepCoolMystique\":{\"gifSlot\":\"\"}}}", Nexus.Service.Serialization.PersistenceJsonContext.Default.NexusSettings)!;
        Assert.True(saved.Devices.DeepCoolMystique.GifSlotUnsure);
        store.Update(s => s.Devices.DeepCoolMystique.GifSlotUnsure = false);
        using var sure = JsonDocument.Parse(await client.GetStringAsync("/devices/deepcool-mystique/state"));
        Assert.False(sure.RootElement.GetProperty("gifSlotUnsure").GetBoolean());
    }

    [Fact]
    public async Task Showing_a_gif_needs_the_screen_and_a_listed_gif()
    {
        using var factory = new NexusAppFactory();
        var client = Client(factory);

        var res = await client.PostAsync("/devices/deepcool-mystique/media/acd9325521f3493c86bfc0b0aa85658d/show", null);
        using var body = JsonDocument.Parse(await res.Content.ReadAsStringAsync());
        Assert.True(body.RootElement.GetProperty("error").GetBoolean());
        Assert.Equal(HttpStatusCode.NotFound, (await client.PostAsync("/devices/deepcool-mystique/media/not-an-id/show", null)).StatusCode);
    }

    [Fact]
    public async Task Progress_is_empty_while_no_upload_runs_and_follows_what_the_worker_reports()
    {
        using var factory = new NexusAppFactory();
        var client = Client(factory);
        var worker = factory.Services.GetRequiredService<MystiqueWorker>();

        using (var idle = JsonDocument.Parse(await client.GetStringAsync("/devices/deepcool-mystique/media/progress")))
        {
            Assert.Equal("", idle.RootElement.GetProperty("phase").GetString());
        }
        worker.ReportProgress(MystiqueWorker.ProgressSending, 50, 200);
        using (var sending = JsonDocument.Parse(await client.GetStringAsync("/devices/deepcool-mystique/media/progress")))
        {
            Assert.Equal("sending", sending.RootElement.GetProperty("phase").GetString());
            Assert.Equal(25, sending.RootElement.GetProperty("percent").GetInt32());
        }
        worker.ClearProgress();
        Assert.Equal(new MystiqueProgress("", 0), worker.Progress);
    }

    [Fact]
    public async Task Removing_everything_needs_the_screen_and_keeps_the_list_without_it()
    {
        using var factory = new NexusAppFactory();
        var client = Client(factory);
        var store = factory.Services.GetRequiredService<IConfigStore>();
        store.Update(s => s.Devices.DeepCoolMystique.Media = new()
        {
            new DeepCoolMystiqueMediaItem { Kind = "picture", Id = "2101b6e6277841daa821e8d79a5c528b" },
        });

        var res = await client.DeleteAsync("/devices/deepcool-mystique/media");

        using var body = JsonDocument.Parse(await res.Content.ReadAsStringAsync());
        Assert.True(body.RootElement.GetProperty("error").GetBoolean());
        var m = new DeepCoolMystiqueSettings();
        store.Read(s => m = s.Devices.DeepCoolMystique.Copy());
        Assert.Single(m.Media);
    }

    [Theory]
    [InlineData("short", 64, "short")]
    [InlineData("abc\U0001F600", 4, "abc")]
    [InlineData("abc\U0001F600", 5, "abc\U0001F600")]
    public void Names_are_cut_without_splitting_a_surrogate_pair(string name, int max, string expected)
    {
        Assert.Equal(expected, DevicesRoutes.TruncateName(name, max));
    }

    [Fact]
    public async Task An_upload_with_no_screen_connected_is_refused_before_any_encode()
    {
        using var factory = new NexusAppFactory();
        var client = Client(factory);
        using var form = new MultipartFormDataContent
        {
            { new ByteArrayContent(new byte[] { 1, 2, 3 }), "file", "a.png" },
            { new StringContent("picture"), "kind" },
        };

        var res = await client.PostAsync("/devices/deepcool-mystique/media", form);

        using var body = JsonDocument.Parse(await res.Content.ReadAsStringAsync());
        Assert.True(body.RootElement.GetProperty("error").GetBoolean());
        Assert.Empty(factory.Services.GetRequiredService<IConfigStore>().Load().Devices.DeepCoolMystique.Media);
    }

    [Fact]
    public void A_sensor_pick_changes_no_command_group()
    {
        var m = new DeepCoolMystiqueSettings();

        DevicesRoutes.ApplyMystiqueRequest(m, new DeepCoolMystiqueSettingsRequest
        {
            Pump = new DeepCoolMystiqueSensorDto { Category = "motherboard", Sensor = "fan/opt" },
        });

        Assert.Equal("fan/opt", m.Pump.Sensor);
        Assert.False(m.ModeEdited || m.ReadoutEdited || m.RecordingEdited || m.DeviceEdited);
    }

    [Fact]
    public void The_gyroscope_starts_off_as_the_screen_ships()
    {
        Assert.False(new DeepCoolMystiqueSettings().Gyroscope);
    }

    [Theory]
    [InlineData(true, false, false, "images", true, "gif")]
    [InlineData(true, false, false, "gif", true, "gif")]
    [InlineData(true, false, false, "images", false, "gif")]
    [InlineData(true, false, true, "images", true, "images")]
    [InlineData(true, true, false, "images", true, "gif")]
    [InlineData(true, true, false, "gif", false, "gif")]
    [InlineData(true, true, false, "gif", true, null)]
    [InlineData(true, true, true, "images", true, null)]
    [InlineData(false, false, false, "gif", true, "images")]
    [InlineData(false, true, false, "gif", true, null)]
    [InlineData(false, true, false, "images", false, null)]
    public void After_a_stream_a_new_item_or_a_gif_shown_over_the_pictures_selects_its_kind_unless_the_user_chose_meanwhile(
        bool isGif, bool inPlace, bool chosenDuringStream, string stored, bool storedChosen, string? sent)
    {
        var m = new DeepCoolMystiqueSettings { MediaSource = stored, SourceEdited = storedChosen };

        var after = DevicesRoutes.MystiqueSourceAfterUpload(m, isGif, inPlace, chosenDuringStream);
        Assert.Equal(sent, after?.Source);
        Assert.True(after?.Send ?? true);
        Assert.Equal(sent ?? stored, m.MediaSource);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void After_a_format_the_first_new_picture_selects_pictures_unsent_and_the_next_new_item_sends(bool gifFirst)
    {
        var m = new DeepCoolMystiqueSettings { MediaSource = "gif", SourceEdited = false, QuietSinceFormat = true };

        Assert.Null(DevicesRoutes.MystiqueSourceAfterUpload(m, isGif: false, inPlace: true, chosenDuringStream: false));
        Assert.True(m.QuietSinceFormat);
        var first = DevicesRoutes.MystiqueSourceAfterUpload(m, gifFirst, inPlace: false, chosenDuringStream: false);
        Assert.Equal(gifFirst ? new MystiqueSourceAfter("gif", true) : new MystiqueSourceAfter("images", false), first);
        Assert.Equal((first!.Value.Source, true), (m.MediaSource, m.SourceEdited));
        Assert.False(m.QuietSinceFormat);
        Assert.Equal(new MystiqueSourceAfter("images", true), DevicesRoutes.MystiqueSourceAfterUpload(m, isGif: false, inPlace: false, chosenDuringStream: false));
    }

    [Fact]
    public void With_no_stored_id_a_show_resends_the_picture_under_its_own_id_and_records_it()
    {
        const string fresh = "41c9bf307cba45a1aa3a1d92bbe93b96";
        var m = new DeepCoolMystiqueSettings { PictureIds = new() { Third } };
        var item = new DeepCoolMystiqueMediaItem { Kind = "picture", Id = First, ScreenId = fresh, Unsent = true };
        m.Media.Add(item);
        Assert.Empty(DevicesRoutes.MystiquePictureOrder(m));

        string slot = DevicesRoutes.MystiqueShowSlot(m, item);
        var plan = DevicesRoutes.MystiquePicturePlan(m, item, slot);

        Assert.Equal(fresh, slot);
        Assert.Equal(new[] { (First, fresh) }, plan.ConvertAll(p => (p.Item.Id, p.ScreenId)));
        DevicesRoutes.RecordMystiquePictureStreams(m, plan.ConvertAll(p => (p.Item.Id, p.ScreenId)), 1);
        Assert.Equal(new[] { fresh }, DevicesRoutes.MystiquePictureOrder(m));
        Assert.Equal(First, DevicesRoutes.MystiqueShownPicture(m));
    }

    [Fact]
    public void A_screen_id_given_to_a_picture_counts_as_stored_only_once_a_stream_reaches_it()
    {
        const string fresh = "41c9bf307cba45a1aa3a1d92bbe93b96";
        var m = new DeepCoolMystiqueSettings { PictureIds = new() { Clicked, First } };
        m.Media.Add(new DeepCoolMystiqueMediaItem { Kind = "picture", Id = Clicked });
        m.Media.Add(new DeepCoolMystiqueMediaItem { Kind = "picture", Id = First, ScreenId = fresh, Unsent = true });

        Assert.Equal(new[] { Clicked }, DevicesRoutes.MystiquePictureOrder(m));

        DevicesRoutes.RecordMystiquePictureStreams(m, new[] { (First, fresh) }, 1);

        Assert.Equal(new[] { Clicked, fresh }, DevicesRoutes.MystiquePictureOrder(m));
        Assert.Equal(First, DevicesRoutes.MystiqueShownPicture(m));
    }

    [Fact]
    public void Choosing_a_source_ends_the_quiet_after_a_format()
    {
        var m = new DeepCoolMystiqueSettings { QuietSinceFormat = true };

        DevicesRoutes.ApplyMystiqueRequest(m, new DeepCoolMystiqueSettingsRequest { MediaSource = DeepCoolMystiqueSettings.SourceImages });

        Assert.False(m.QuietSinceFormat);
    }

    [Fact]
    public void Picking_the_same_source_again_still_counts_as_a_choice()
    {
        var m = new DeepCoolMystiqueSettings { MediaSource = DeepCoolMystiqueSettings.SourceImages, SourceEdited = true };
        int before = DevicesRoutes.MystiqueSourceChoices;

        DevicesRoutes.ApplyMystiqueRequest(m, new DeepCoolMystiqueSettingsRequest { MediaSource = DeepCoolMystiqueSettings.SourceImages });

        Assert.NotEqual(before, DevicesRoutes.MystiqueSourceChoices);
    }

    [Fact]
    public void A_failed_upload_frees_its_id_once_and_leaves_listed_items_alone()
    {
        using var factory = new NexusAppFactory();
        var store = factory.Services.GetRequiredService<IConfigStore>();
        store.Update(s => s.Devices.DeepCoolMystique.Media = new() { new DeepCoolMystiqueMediaItem { Kind = "picture", Id = "2101b6e6277841daa821e8d79a5c528b" } });

        DevicesRoutes.FreeFailedMystiqueId(store, "picture", "acd9325521f3493c86bfc0b0aa85658d");
        DevicesRoutes.FreeFailedMystiqueId(store, "picture", "acd9325521f3493c86bfc0b0aa85658d");
        DevicesRoutes.FreeFailedMystiqueId(store, "picture", "2101b6e6277841daa821e8d79a5c528b");

        var m = new DeepCoolMystiqueSettings();
        store.Read(s => m = s.Devices.DeepCoolMystique.Copy());
        Assert.Equal(new[] { ("picture", "acd9325521f3493c86bfc0b0aa85658d") }, m.Freed.Select(i => (i.Kind, i.Id)));
        Assert.Single(m.Media);
        Assert.True(m.Media[0].Unsent);
        Assert.Equal("2101b6e6277841daa821e8d79a5c528b", DevicesRoutes.MystiquePictureSlot(m));
        Assert.Equal((MystiqueMedia.IdOf(MystiqueMedia.KindPicture, new byte[] { 7 }), "acd9325521f3493c86bfc0b0aa85658d"), DevicesRoutes.MystiqueUploadId(store, "picture", new byte[] { 7 }, ""));
    }

    [Theory]
    [InlineData(false, false, true)]
    [InlineData(false, true, false)]
    [InlineData(true, false, true)]
    [InlineData(true, true, false)]
    public void Media_stream_half_a_turn_round_while_the_screen_holds_gyroscope_byte_one(bool deviceEdited, bool gyroscope, bool rotates)
    {
        var m = new DeepCoolMystiqueSettings { DeviceEdited = deviceEdited, Gyroscope = gyroscope };

        Assert.Equal(rotates, DevicesRoutes.MystiqueRotatesMedia(m));
    }

    [Fact]
    public void The_screen_ships_with_nexus_control_off_until_the_user_turns_it_on()
    {
        Assert.False(DeviceControlPolicy.DefaultOn("deepcool-mystique"));
    }
}
