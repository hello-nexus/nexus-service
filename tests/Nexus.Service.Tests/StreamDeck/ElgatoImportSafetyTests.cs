using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Nexus.Service.Activity;
using Nexus.Service.Deck;
using Nexus.Service.Models.Activity;
using Nexus.Service.Peripherals.StreamDeck.ElgatoImport;
using Xunit;

namespace Nexus.Service.Tests.StreamDeck;

/// <summary>Untrusted Open paths are never touched on disk, and a volume dial keeps the id of an endpoint this PC knows.</summary>
public sealed class ElgatoImportSafetyTests
{
    private sealed class FakeAudioDevices : IAudioDeviceProvider
    {
        public AudioDeviceList List { get; set; } = new();
        public AudioKnownEndpointIds Known { get; set; } = new();
        public int Reads { get; private set; }
        public AudioDeviceList ListDevices()
        {
            Reads++;
            return List;
        }
        public AudioKnownEndpointIds ListKnownEndpointIds() => Known;
        public bool SetDefaultOutput(string deviceId) => true;
        public bool SetDefaultInput(string deviceId) => true;
        public bool SetSpatial(string deviceId, string formatId) => true;
    }

    private static ElgatoProfileTranslator Translator(IAudioDeviceProvider? devices = null) =>
        new(new DeckImageStore(Path.Combine(Path.GetTempPath(), "nexus-elgato-safety-" + Guid.NewGuid().ToString("N")[..8])), devices);

    private static JsonElement Json(object o) => JsonDocument.Parse(JsonSerializer.Serialize(o)).RootElement.Clone();

    private static ElgatoProfile KeyProfile(string uuid, object settings)
    {
        var profile = new ElgatoProfile { Model = "20GAI9901" };
        var page = new ElgatoPageData();
        page.Actions[(0, 0)] = new ElgatoActionData { Uuid = uuid, Name = "A", Settings = Json(settings) };
        profile.TopPageIds.Add("only");
        profile.PagesById["only"] = page;
        return profile;
    }

    private static ElgatoProfile DialProfile(params (string Uuid, object Settings)[] dials)
    {
        var profile = new ElgatoProfile { Model = "20GAT9901" };
        var page = new ElgatoPageData();
        for (var i = 0; i < dials.Length; i++)
        {
            var action = new ElgatoActionData { Uuid = dials[i].Uuid, Name = "D" + i, Settings = Json(dials[i].Settings) };
            page.Dials[i] = action;
            page.DialEntries[(i, 0)] = action;
        }
        profile.TopPageIds.Add("only");
        profile.PagesById["only"] = page;
        return profile;
    }

    [Theory]
    [InlineData("\\\\evil-host\\share\\payload.exe")]
    [InlineData("//evil-host/share/payload.exe")]
    [InlineData("\\\\.\\pipe\\x")]
    [InlineData("/\\host\\share\\x.exe")]
    [InlineData("\\/host/share")]
    [InlineData("\\\\?\\C:\\x")]
    [InlineData("relative\\dir\\")]
    [InlineData("..\\up")]
    [InlineData("file.txt")]
    public void OpenAction_NonLocalRootedPath_IsUnsupported(string path)
    {
        var (config, report) = Translator().Translate(KeyProfile("com.elgato.streamdeck.system.open", new { path }));
        Assert.Null(config.Pages[0].Slots[0].Action);
        Assert.Contains(report.Unmapped, e => e.Reason == "unsupported");
    }

    [Theory]
    [InlineData("\\\\evil-host\\share\\app.exe")]
    [InlineData("relative.exe")]
    public void OpenApplication_NonLocalRootedFallbackPath_IsUnsupported(string path)
    {
        var (config, report) = Translator().Translate(KeyProfile("com.elgato.streamdeck.system.openapp", new { path }));
        Assert.Null(config.Pages[0].Slots[0].Action);
        Assert.Contains(report.Unmapped, e => e.Reason == "unsupported");
    }

    [Fact]
    public void OpenAction_LocalRootedPath_StillMaps()
    {
        var (config, _) = Translator().Translate(KeyProfile("com.elgato.streamdeck.system.open", new { path = "C:\\docs\\a.txt" }));
        Assert.Equal("openFile", config.Pages[0].Slots[0].Action!.Type);
    }

    private static FakeAudioDevices Devices() => new()
    {
        List = new AudioDeviceList
        {
            Outputs = { new AudioDevice { Id = "{ACTIVE}", Name = "Speakers" } },
            Inputs = { new AudioDevice { Id = "{MIC}", Name = "Mic", Direction = "input" } },
        },
        Known = { Outputs = { "{ACTIVE}", "{Unplugged}" } },
    };

    [Fact]
    public void VolumeDial_UnpluggedKnownDevice_KeepsItsIdCaseInsensitively()
    {
        var devices = Devices();
        var (config, report) = Translator(devices).Translate(DialProfile(
            ("com.elgato.volume-controller.output-device-control", new { deviceId = "{UNPLUGGED}" })));
        Assert.Equal("{UNPLUGGED}", config.Pages[0].Dials![0].Action!.DeviceId);
        Assert.DoesNotContain(report.Unmapped, e => e.Reason == "deviceReset");
    }

    [Fact]
    public void VolumeDial_NeverSeenDevice_BecomesDefaultWithReportNote()
    {
        var (config, report) = Translator(Devices()).Translate(DialProfile(
            ("com.elgato.volume-controller.output-device-control", new { deviceId = "{OTHER-PC}" }),
            ("com.elgato.volume-controller.input-device-control", new { deviceId = "{OTHER-MIC}" })));
        Assert.Null(config.Pages[0].Dials![0].Action!.DeviceId);
        Assert.Null(config.Pages[0].Dials![1].Action!.DeviceId);
        Assert.Equal(2, report.Unmapped.Count(e => e.Reason == "deviceReset"));
    }

    [Fact]
    public void VolumeDial_DeviceListIsReadOncePerTranslate()
    {
        var devices = Devices();
        var translator = Translator(devices);
        translator.Translate(DialProfile(
            ("com.elgato.volume-controller.output-device-control", new { deviceId = "{ACTIVE}" }),
            ("com.elgato.volume-controller.input-device-control", new { deviceId = "{MIC}" })));
        Assert.Equal(1, devices.Reads);
        translator.Translate(DialProfile(("com.elgato.volume-controller.output-device-control", new { deviceId = "{ACTIVE}" })));
        Assert.Equal(2, devices.Reads);
    }

    [Fact]
    public void DialStack_DroppedStepAndDeviceReset_BothStayInTheReport()
    {
        var profile = DialProfile(("com.elgato.streamdeck.dial.stack", new { }));
        var stack = profile.PagesById["only"].Dials[0];
        stack.Children.Add(new ElgatoActionData
        {
            Uuid = "com.elgato.volume-controller.output-device-control",
            Settings = Json(new { deviceId = "{OTHER-PC}" }),
        });
        stack.Children.Add(new ElgatoActionData { Uuid = "com.example.plugin.dial", Settings = Json(new { }) });
        var (_, report) = Translator(Devices()).Translate(profile);
        var entry = Assert.Single(report.Unmapped, e => e.Dial == 1);
        Assert.Equal("multiStep", entry.Reason);
        Assert.Contains("device reset", entry.Detail);
    }

    [Fact]
    public async Task ConcurrentTranslates_DoNotShareDeviceListState()
    {
        var translator = Translator(Devices());
        var tasks = Enumerable.Range(0, 8).Select(_ => Task.Run(() => translator.Translate(DialProfile(
            ("com.elgato.volume-controller.output-device-control", new { deviceId = "{OTHER-PC}" })))));
        foreach (var (config, report) in await Task.WhenAll(tasks))
        {
            Assert.Null(config.Pages[0].Dials![0].Action!.DeviceId);
            Assert.Single(report.Unmapped, e => e.Reason == "deviceReset");
        }
    }

    [Fact]
    public void VolumeDial_EmptyDeviceList_KeepsTheId()
    {
        var (config, report) = Translator(new FakeAudioDevices()).Translate(DialProfile(
            ("com.elgato.volume-controller.output-device-control", new { deviceId = "{ANY}" })));
        Assert.Equal("{ANY}", config.Pages[0].Dials![0].Action!.DeviceId);
        Assert.DoesNotContain(report.Unmapped, e => e.Reason == "deviceReset");
    }
}
