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

public class DeepCoolMystiqueRoutesTests
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
        store.Update(s => s.Devices.DeepCoolMystique.PictureSlot = first);

        Assert.Equal(HttpStatusCode.OK, (await client.DeleteAsync($"/devices/deepcool-mystique/media/{second}")).StatusCode);

        var m = new DeepCoolMystiqueSettings();
        store.Read(s => m = s.Devices.DeepCoolMystique.Copy());
        Assert.Equal(new[] { first }, m.Freed.ConvertAll(i => i.Id));
        Assert.Equal(first, m.PictureSlot);
        Assert.Equal(second, DevicesRoutes.MystiqueScreenId(m.Media.Single()));
    }

    [Fact]
    public void The_first_picture_place_is_the_recorded_one_else_the_first_listed_else_a_freed_one()
    {
        const string a = "2101b6e6277841daa821e8d79a5c528b";
        const string b = "acd9325521f3493c86bfc0b0aa85658d";
        var m = new DeepCoolMystiqueSettings();
        Assert.Null(DevicesRoutes.MystiquePictureSlot(m));

        m.Freed.Add(new DeepCoolMystiqueMediaItem { Kind = "picture", Id = b });
        Assert.Equal(b, DevicesRoutes.MystiquePictureSlot(m));

        m.Media.Add(new DeepCoolMystiqueMediaItem { Kind = "gif", Id = a });
        m.Media.Add(new DeepCoolMystiqueMediaItem { Kind = "picture", Id = a, ScreenId = b });
        Assert.Equal(b, DevicesRoutes.MystiquePictureSlot(m));
        m.Media[1].ScreenId = "";
        Assert.Equal(a, DevicesRoutes.MystiquePictureSlot(m));

        m.PictureSlot = b;
        Assert.Equal(b, DevicesRoutes.MystiquePictureSlot(m));
    }

    [Theory]
    [InlineData(2, true)]
    [InlineData(1, true)]
    [InlineData(1, false)]
    [InlineData(0, true)]
    public void A_picture_swap_records_what_streamed(int streamed, bool displacedListed)
    {
        const string clicked = "2101b6e6277841daa821e8d79a5c528b";
        const string first = "acd9325521f3493c86bfc0b0aa85658d";
        var m = new DeepCoolMystiqueSettings();
        m.Media.Add(new DeepCoolMystiqueMediaItem { Kind = "picture", Id = clicked });
        if (displacedListed)
        {
            m.Media.Add(new DeepCoolMystiqueMediaItem { Kind = "picture", Id = first });
        }
        else
        {
            m.Freed.Add(new DeepCoolMystiqueMediaItem { Kind = "picture", Id = first });
        }

        DevicesRoutes.SetMystiquePictureSwap(m, clicked, first, clicked, displacedListed ? first : null, streamed);

        if (streamed == 0)
        {
            Assert.Equal("", m.PictureSlot);
            Assert.All(m.Media, i => Assert.Equal("", i.ScreenId));
            return;
        }
        Assert.Equal(first, m.PictureSlot);
        Assert.Equal(first, DevicesRoutes.MystiqueScreenId(m.Media[0]));
        if (displacedListed)
        {
            Assert.Equal(clicked, DevicesRoutes.MystiqueScreenId(m.Media[1]));
            Assert.Empty(m.Freed);
        }
        else
        {
            Assert.Equal(new[] { clicked }, m.Freed.ConvertAll(i => i.Id));
        }
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

        Assert.Equal(sent, DevicesRoutes.MystiqueSourceAfterUpload(m, isGif, inPlace, chosenDuringStream));
        Assert.Equal(sent ?? stored, m.MediaSource);
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
