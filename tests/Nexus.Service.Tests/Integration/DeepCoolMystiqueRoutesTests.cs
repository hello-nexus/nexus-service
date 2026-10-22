using System.Collections.Generic;
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
        Assert.Equal(held[1].Id, DevicesRoutes.MystiqueUploadId(store, kind, fresh, held[1].Id));
        Assert.Equal(held[0].Id, DevicesRoutes.MystiqueUploadId(store, kind, new byte[] { 0 }, ""));

        store.Update(s => s.Devices.DeepCoolMystique.Media.RemoveAt(max - 1));
        Assert.Equal(MystiqueMedia.IdOf(wireKind, fresh), DevicesRoutes.MystiqueUploadId(store, kind, fresh, ""));
    }

    [Fact]
    public async Task Removing_media_frees_its_id_for_the_next_upload_of_that_kind()
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

        Assert.Equal(HttpStatusCode.OK, (await client.DeleteAsync($"/devices/deepcool-mystique/media/{pictureId}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.DeleteAsync($"/devices/deepcool-mystique/media/{pictureId}")).StatusCode);
        Assert.Equal(pictureId, DevicesRoutes.MystiqueUploadId(store, "picture", new byte[] { 9 }, ""));
        Assert.NotEqual(pictureId, DevicesRoutes.MystiqueUploadId(store, "gif", new byte[] { 9 }, ""));

        Assert.Equal(HttpStatusCode.OK, (await client.DeleteAsync($"/devices/deepcool-mystique/media/{gifId}")).StatusCode);
        var m = new DeepCoolMystiqueSettings();
        store.Read(s => m = s.Devices.DeepCoolMystique.Copy());
        Assert.Empty(m.Media);
        Assert.Equal(new[] { pictureId, gifId }, m.Freed.ConvertAll(i => i.Id));
        Assert.Equal(gifId, DevicesRoutes.MystiqueUploadId(store, "gif", new byte[] { 9 }, ""));
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

    [Fact]
    public void The_screen_ships_with_nexus_control_off_until_the_user_turns_it_on()
    {
        Assert.False(DeviceControlPolicy.DefaultOn("deepcool-mystique"));
    }
}
