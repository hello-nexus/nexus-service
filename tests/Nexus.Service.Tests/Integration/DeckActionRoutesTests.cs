using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Nexus.Service.Activity;
using Nexus.Service.Auth;
using Nexus.Service.Models.Activity;
using Nexus.Service.Models.Peripherals.Keeb;
using Nexus.Service.Peripherals.Keeb;
using Nexus.Service.Platform.Power;
using Xunit;

namespace Nexus.Service.Tests.Integration;

/// <summary>
/// End-to-end coverage for the deck's new /system/* action routes: real request
/// pipeline (auth + source-gen JSON + routing) with hardware providers swapped
/// for fakes at the DI seam, asserting each route deserializes its body and
/// drives the provider with the expected arguments.
/// </summary>
public class DeckActionRoutesTests
{
    private sealed class FakeInputter : IInputterProvider
    {
        public InputterBody? Last;
        public void Send(InputterBody body) => Last = body;
    }

    private sealed class FakeClipboard : Nexus.Service.Platform.Clipboard.IClipboardProvider
    {
        public string? Last;
        public bool SetText(string text) { Last = text; return true; }
    }

    private sealed class FakePower : ISystemPowerProvider
    {
        public string? Called;
        public bool Lock() { Called = "lock"; return true; }
        public bool Sleep() { Called = "sleep"; return true; }
        public bool Shutdown() { Called = "shutdown"; return true; }
        public bool Restart() { Called = "restart"; return true; }
        public bool Logout() { Called = "logout"; return true; }
    }

    private sealed class FakeAudio : IAudioDeviceProvider
    {
        public string? SetOutput;
        public string? SetInput;
        public (string DeviceId, string FormatId)? SetSpatialCall;
        public AudioDeviceList ListDevices() => new()
        {
            Outputs = { new AudioDevice { Id = "spk", Name = "Speakers", Direction = "output", IsDefault = true } },
            Inputs = { new AudioDevice { Id = "mic", Name = "Microphone", Direction = "input" } },
            Spatial = new AudioSpatialState
            {
                Supported = true,
                DeviceId = "spk",
                Formats = { new AudioSpatialFormat { Id = "sonic", Name = "Windows Sonic for Headphones" } },
            },
        };
        public bool SetDefaultOutput(string deviceId) { SetOutput = deviceId; return true; }
        public bool SetDefaultInput(string deviceId) { SetInput = deviceId; return true; }
        // Mirrors the Windows provider: only an offered format (or off) lands.
        public bool SetSpatial(string deviceId, string formatId)
        {
            SetSpatialCall = (deviceId, formatId);
            return formatId.Length == 0 || formatId == "sonic";
        }
    }

    private readonly FakeInputter _inputter = new();
    private readonly FakeClipboard _clipboard = new();
    private readonly FakePower _power = new();
    private readonly FakeAudio _audio = new();

    private (WebApplicationFactory<Program> factory, HttpClient client) Boot()
    {
        var factory = new NexusAppFactory().WithWebHostBuilder(b =>
            b.ConfigureTestServices(s =>
            {
                s.RemoveAll<IInputterProvider>();
                s.AddSingleton<IInputterProvider>(_inputter);
                s.RemoveAll<Nexus.Service.Platform.Clipboard.IClipboardProvider>();
                s.AddSingleton<Nexus.Service.Platform.Clipboard.IClipboardProvider>(_clipboard);
                s.RemoveAll<ISystemPowerProvider>();
                s.AddSingleton<ISystemPowerProvider>(_power);
                s.RemoveAll<IAudioDeviceProvider>();
                s.AddSingleton<IAudioDeviceProvider>(_audio);
            }));
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", factory.Services.GetRequiredService<TokenService>().Token);
        return (factory, client);
    }

    private static StringContent Json(string body) => new(body, Encoding.UTF8, "application/json");

    [Fact]
    public async Task InputKeys_chord_expands_to_down_and_up_strokes()
    {
        var (factory, client) = Boot();
        using (factory)
        {
            var res = await client.PostAsync("/system/input/keys", Json("{\"key\":\"KeyM\",\"ctrl\":true,\"shift\":true}"));
            Assert.True(res.IsSuccessStatusCode);
            Assert.NotNull(_inputter.Last);
            Assert.Equal(2, _inputter.Last!.Strokes.Count);
            Assert.Equal("KeyM", _inputter.Last.Strokes[0].Key);
            Assert.True(_inputter.Last.Strokes[0].Ctrl);
            Assert.True(_inputter.Last.Strokes[0].Shift);
            Assert.Equal("keydown", _inputter.Last.Strokes[0].Type);
            Assert.Equal("keyup", _inputter.Last.Strokes[1].Type);
        }
    }

    [Fact]
    public async Task InputKeys_requires_a_key_or_strokes()
    {
        var (factory, client) = Boot();
        using (factory)
        {
            var res = await client.PostAsync("/system/input/keys", Json("{}"));
            var text = await res.Content.ReadAsStringAsync();
            Assert.Contains("\"error\":true", text);
            Assert.Null(_inputter.Last);
        }
    }

    [Fact]
    public async Task InputKeys_requires_auth()
    {
        var (factory, _) = Boot();
        using (factory)
        {
            var anon = factory.CreateClient();
            var res = await anon.PostAsync("/system/input/keys", Json("{\"key\":\"KeyM\"}"));
            Assert.False(res.IsSuccessStatusCode);
            Assert.Null(_inputter.Last);
        }
    }

    [Fact]
    public async Task InputText_SetsClipboardAndPastes()
    {
        var (factory, client) = Boot();
        using (factory)
        {
            var res = await client.PostAsync("/system/input/text", Json("{\"text\":\"hello\"}"));
            Assert.True(res.IsSuccessStatusCode);
            Assert.Equal("hello", _clipboard.Last);
            Assert.NotNull(_inputter.Last);
            Assert.Equal(2, _inputter.Last!.Strokes.Count);
            Assert.Equal("KeyV", _inputter.Last.Strokes[0].Key);
        }
    }

    [Fact]
    public async Task InputText_requires_auth()
    {
        var (factory, _) = Boot();
        using (factory)
        {
            var anon = factory.CreateClient();
            var res = await anon.PostAsync("/system/input/text", Json("{\"text\":\"hello\"}"));
            Assert.False(res.IsSuccessStatusCode);
            Assert.Null(_clipboard.Last);
        }
    }

    [Fact]
    public async Task PowerLock_invokes_the_provider()
    {
        var (factory, client) = Boot();
        using (factory)
        {
            var res = await client.PostAsync("/system/power/lock", Json("{}"));
            Assert.True(res.IsSuccessStatusCode);
            Assert.Equal("lock", _power.Called);
        }
    }

    [Fact]
    public async Task AudioDevices_lists_and_switches_default_output()
    {
        var (factory, client) = Boot();
        using (factory)
        {
            var list = await client.GetAsync("/system/audio/devices");
            var listText = await list.Content.ReadAsStringAsync();
            Assert.Contains("Speakers", listText);
            Assert.Contains("Microphone", listText);

            var set = await client.PostAsync("/system/audio/default-output", Json("{\"deviceId\":\"spk\"}"));
            Assert.True(set.IsSuccessStatusCode);
            Assert.Equal("spk", _audio.SetOutput);
        }
    }

    [Fact]
    public async Task AudioSpatial_lists_formats_and_switches_them()
    {
        var (factory, client) = Boot();
        using (factory)
        {
            var list = await client.GetAsync("/system/audio/devices");
            var listText = await list.Content.ReadAsStringAsync();
            Assert.Contains("\"spatial\":{\"supported\":true", listText);
            Assert.Contains("Windows Sonic for Headphones", listText);

            var on = await client.PostAsync("/system/audio/spatial", Json("{\"deviceId\":\"spk\",\"formatId\":\"sonic\"}"));
            Assert.True(on.IsSuccessStatusCode);
            Assert.DoesNotContain("\"error\":true", await on.Content.ReadAsStringAsync());
            Assert.Equal(("spk", "sonic"), _audio.SetSpatialCall);

            var off = await client.PostAsync("/system/audio/spatial", Json("{\"deviceId\":\"spk\",\"formatId\":\"\"}"));
            Assert.True(off.IsSuccessStatusCode);
            Assert.Equal(("spk", ""), _audio.SetSpatialCall);

            var bad = await client.PostAsync("/system/audio/spatial", Json("{\"deviceId\":\"spk\",\"formatId\":\"nope\"}"));
            Assert.Contains("\"error\":true", await bad.Content.ReadAsStringAsync());
        }
    }

    [Fact]
    public async Task OpenUrl_rejects_a_non_http_url()
    {
        var (factory, client) = Boot();
        using (factory)
        {
            var res = await client.PostAsync("/system/open-url", Json("{\"url\":\"file:///etc/passwd\"}"));
            var text = await res.Content.ReadAsStringAsync();
            Assert.Contains("\"error\":true", text);
        }
    }

    [Fact]
    public async Task SystemRoutes_require_auth()
    {
        var (factory, _) = Boot();
        using (factory)
        {
            var anon = factory.CreateClient();
            var res = await anon.PostAsync("/system/power/lock", Json("{}"));
            Assert.False(res.IsSuccessStatusCode);
            Assert.Null(_power.Called);
        }
    }

    [Fact]
    public async Task OpenTaskManager_requires_auth()
    {
        // OpenTaskManager has no injectable provider seam (same as
        // OpenUrlAsync/OpenPathAsync/OpenSettingsAsync) - it shells out to the
        // real OS on a successful dispatch, so only the auth gate is exercised
        // here rather than a real request with a valid token.
        var (factory, _) = Boot();
        using (factory)
        {
            var anon = factory.CreateClient();
            var res = await anon.PostAsync("/system/open-task-manager", Json("{}"));
            Assert.False(res.IsSuccessStatusCode);
        }
    }
}
