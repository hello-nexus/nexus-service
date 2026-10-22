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

    [Fact]
    public void Uploads_reuse_the_gif_slot_and_stop_at_the_picture_slots()
    {
        using var factory = new NexusAppFactory();
        var store = factory.Services.GetRequiredService<IConfigStore>();
        var a = new byte[] { 1 };
        var b = new byte[] { 2 };
        var c = new byte[] { 3 };
        string idA = MystiqueMedia.IdOf(MystiqueMedia.KindPicture, a);
        string idB = MystiqueMedia.IdOf(MystiqueMedia.KindPicture, b);
        const string gifId = "acd9325521f3493c86bfc0b0aa85658d";
        store.Update(s => s.Devices.DeepCoolMystique.Media = new()
        {
            new DeepCoolMystiqueMediaItem { Kind = "picture", Id = idA },
            new DeepCoolMystiqueMediaItem { Kind = "picture", Id = idB },
            new DeepCoolMystiqueMediaItem { Kind = "gif", Id = gifId },
        });

        Assert.Equal(gifId, DevicesRoutes.MystiqueUploadId(store, "gif", c, ""));
        Assert.Null(DevicesRoutes.MystiqueUploadId(store, "picture", c, ""));
        Assert.Equal(idB, DevicesRoutes.MystiqueUploadId(store, "picture", c, idB));
        Assert.Equal(idA, DevicesRoutes.MystiqueUploadId(store, "picture", a, ""));
        Assert.Null(DevicesRoutes.MystiqueUploadId(store, "picture", c, gifId));
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
    public void The_screen_ships_with_nexus_control_off_until_the_user_turns_it_on()
    {
        Assert.False(DeviceControlPolicy.DefaultOn("deepcool-mystique"));
    }
}
