using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Nexus.Service.Platform.Linux;
using Xunit;

namespace Nexus.Service.Tests.Platform;

public class HyprlandPanelTests
{
    private const string MonitorsJson = """
[{"id":0,"name":"DP-1","width":3440,"height":1440,"transform":0,"focused":true,
  "activeWorkspace":{"address":"0x1","id":1,"type":"regular","name":"1"}},
 {"id":1,"name":"DP-2","width":3840,"height":1100,"transform":3,"focused":false,
  "activeWorkspace":{"address":"0x2","id":4,"type":"regular","name":"4"}}]
""";

    private static string Clients(string address, string cls, int monitor, int fullscreen, bool mapped = true) =>
        $$"""[{"address":"{{address}}","mapped":{{(mapped ? "true" : "false")}},"class":"{{cls}}","initialClass":"{{cls}}","monitor":{{monitor}},"fullscreen":{{fullscreen}},"fullscreenClient":{{fullscreen}}}]""";

    [Fact]
    public void ParseMonitors_ReadsConnectorTransformAndActiveWorkspace()
    {
        var monitors = HyprlandIpc.ParseMonitors(MonitorsJson);
        Assert.Equal(2, monitors.Count);
        Assert.Equal(new HyprlandIpc.Monitor(1, "DP-2", 3840, 1100, 3, 4, "4"), monitors[1]);
    }

    [Theory]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("{}")]
    public void Parsers_TolerateGarbage(string reply)
    {
        Assert.Empty(HyprlandIpc.ParseMonitors(reply));
        Assert.Empty(HyprlandIpc.ParseClients(reply));
        Assert.Empty(HyprlandIpc.ParseTouchDevices(reply));
        Assert.Null(HyprlandIpc.ParseActiveAddress(reply));
    }

    [Fact]
    public void ParseTouchDevices_ReadsOnlyTheTouchList()
    {
        const string json = """{"mice":[{"address":"0x1","name":"a-mouse"}],"touch":[{"address":"0x2","name":"ilitek-------ilitek-touch"}]}""";
        Assert.Equal(["ilitek-------ilitek-touch"], HyprlandIpc.ParseTouchDevices(json));
    }

    [Fact]
    public void ParseActiveAddress_IsNullForTheEmptyObject()
    {
        Assert.Null(HyprlandIpc.ParseActiveAddress("{}"));
        Assert.Equal("0xabc", HyprlandIpc.ParseActiveAddress("""{"address":"0xabc"}"""));
    }

    [Theory]
    [InlineData("""{"option":"input:touchdevice:output","str":"DP-2","set":true }""", "DP-2")]
    [InlineData("""{"option":"input:touchdevice:output","str":"[[EMPTY]]","set":false }""", null)]
    [InlineData("""{"option":"input:touchdevice:output","str":"","set":true }""", null)]
    [InlineData("no such option", null)]
    public void ParseSetStringOption_OnlyUserSetValues(string reply, string? expected)
        => Assert.Equal(expected, HyprlandIpc.ParseSetStringOption(reply));

    [Theory]
    [InlineData("ILITEK ILITEK-TOUCH", "ilitek-ilitek-touch")]
    [InlineData("Foo, Bar\nBaz", "foo--bar-baz")]
    public void NormalizeDeviceName_MatchesHyprlandInternalForm(string raw, string expected)
        => Assert.Equal(expected, HyprlandIpc.NormalizeDeviceName(raw));

    [Fact]
    public void LuaString_EscapesQuotesAndRejectsControlCharacters()
    {
        Assert.Equal("\"a\\\"b\\\\\"", HyprlandIpc.LuaString("a\"b\\"));
        Assert.Null(HyprlandIpc.LuaString("a\nb"));
    }

    [Theory]
    [InlineData("OK", true)]
    [InlineData("ok", true)]
    [InlineData("unknown request", false)]
    [InlineData("eval is only supported with the lua config manager", false)]
    [InlineData(null, false)]
    public void IsOk(string? reply, bool expected) => Assert.Equal(expected, HyprlandIpc.IsOk(reply?.ToLowerInvariant()));

    [Fact]
    public void IsPanelWindow_MatchesFirefoxAndChromiumClassesOnly()
    {
        static HyprlandIpc.Client C(string cls, bool mapped = true) => new("0x1", cls, cls, 0, mapped, 0, 0);
        Assert.True(LinuxHyprlandPanelPlacement.IsPanelWindow(C("nexus-panel-y70")));
        Assert.True(LinuxHyprlandPanelPlacement.IsPanelWindow(C("chrome-localhost__panel-Default")));
        Assert.True(LinuxHyprlandPanelPlacement.IsPanelWindow(C("brave-localhost__panel-Default")));
        Assert.False(LinuxHyprlandPanelPlacement.IsPanelWindow(C("firefox")));
        Assert.False(LinuxHyprlandPanelPlacement.IsPanelWindow(C("nexus-panel-y70", mapped: false)));
    }

    [Fact]
    public void FindTargetMonitor_PrefersTheConnectorThenTheSingleStrip()
    {
        var monitors = HyprlandIpc.ParseMonitors(MonitorsJson);
        Assert.Equal("DP-1", LinuxHyprlandPanelPlacement.FindTargetMonitor(monitors, "dp-1")?.Name);
        Assert.Equal("DP-2", LinuxHyprlandPanelPlacement.FindTargetMonitor(monitors, "HDMI-A-9")?.Name);
        Assert.Equal("DP-2", LinuxHyprlandPanelPlacement.FindTargetMonitor(monitors, "")?.Name);
        var noStrip = monitors.Take(1).ToList();
        Assert.Null(LinuxHyprlandPanelPlacement.FindTargetMonitor(noStrip, "HDMI-A-9"));
    }

    [Fact]
    public void Plan_WalksNoWindowThenMoveThenFullscreenThenDone()
    {
        var monitors = HyprlandIpc.ParseMonitors(MonitorsJson);
        var plan = (string clients) => LinuxHyprlandPanelPlacement.Plan(HyprlandIpc.ParseClients(clients), monitors, "DP-2");
        Assert.Equal(LinuxHyprlandPanelPlacement.StepKind.NoWindow, plan("[]").Kind);
        Assert.Equal(LinuxHyprlandPanelPlacement.StepKind.Move, plan(Clients("0xa", "nexus-panel-y70", 0, 2)).Kind);
        Assert.Equal(LinuxHyprlandPanelPlacement.StepKind.Fullscreen, plan(Clients("0xa", "nexus-panel-y70", 1, 0)).Kind);
        Assert.Equal(LinuxHyprlandPanelPlacement.StepKind.Done, plan(Clients("0xa", "nexus-panel-y70", 1, 2)).Kind);
    }

    [Fact]
    public void Commands_UseTheTargetWorkspaceAndWindowAddress()
    {
        var target = HyprlandIpc.ParseMonitors(MonitorsJson)[1];
        var ws = LinuxHyprlandPanelPlacement.WorkspaceSelector(target)!;
        Assert.Equal("4", ws);
        Assert.Equal("/dispatch movetoworkspacesilent 4,address:0xa1", LinuxHyprlandPanelPlacement.MoveLegacy("0xa1", ws));
        Assert.Contains("follow = false", LinuxHyprlandPanelPlacement.MoveLua("0xa1", ws), StringComparison.Ordinal);
        Assert.Contains("address:0xa1", LinuxHyprlandPanelPlacement.MoveLua("0xa1", ws), StringComparison.Ordinal);
        Assert.Equal("name:web", LinuxHyprlandPanelPlacement.WorkspaceSelector(target with { WorkspaceId = null, WorkspaceName = "web" }));
        Assert.Null(LinuxHyprlandPanelPlacement.WorkspaceSelector(target with { WorkspaceId = null, WorkspaceName = "a\"b" }));
    }

    [Theory]
    [InlineData("0xabc123", true)]
    [InlineData("0x", false)]
    [InlineData("abc", false)]
    [InlineData("0x1\";os.exit()", false)]
    public void IsAddress_GuardsTheDispatchString(string address, bool expected)
        => Assert.Equal(expected, LinuxHyprlandPanelPlacement.IsAddress(address));

    private sealed class FakeHyprland
    {
        public int KioskMonitor = 0;
        public int Fullscreen = 2;
        public bool LuaAccepted = true;
        public string ActiveAddress = "0xbeef";
        public readonly List<string> Sent = [];

        public string? Send(string request)
        {
            Sent.Add(request);
            if (request == "j/activewindow")
                return $$"""{"address":"{{ActiveAddress}}"}""";
            if (request == "j/monitors")
                return MonitorsJson;
            if (request == "j/clients")
            {
                return "[" + Clients("0xa1", "nexus-panel-y70", KioskMonitor, Fullscreen).Trim('[', ']')
                    + ",{\"address\":\"0xbeef\",\"mapped\":true,\"class\":\"kitty\",\"initialClass\":\"kitty\",\"monitor\":0,\"fullscreen\":0,\"fullscreenClient\":0}]";
            }
            if (request.StartsWith("/dispatch hl.dsp.window.move", StringComparison.Ordinal) && LuaAccepted)
            {
                KioskMonitor = 1;
                ActiveAddress = "0xa1";
                return "ok";
            }
            if (request.StartsWith("/dispatch movetoworkspacesilent", StringComparison.Ordinal))
            {
                KioskMonitor = 1;
                ActiveAddress = "0xa1";
                return "ok";
            }
            if (request.StartsWith("/dispatch hl.dsp.focus", StringComparison.Ordinal) && LuaAccepted)
            {
                ActiveAddress = request.Contains("0xbeef", StringComparison.Ordinal) ? "0xbeef" : ActiveAddress;
                return "ok";
            }
            if (request.StartsWith("/dispatch hl.dsp.window.fullscreen", StringComparison.Ordinal))
            {
                Fullscreen = 2;
                return "ok";
            }
            return LuaAccepted ? "ok" : "unknown request";
        }
    }

    [Fact]
    public async Task PlaceAsync_MovesSilentlyThenRestoresTheUsersFocus()
    {
        var fake = new FakeHyprland();
        Assert.True(await LinuxHyprlandPanelPlacement.PlaceAsync(fake.Send, "DP-2", _ => Task.CompletedTask));
        Assert.Contains(fake.Sent, r => r.StartsWith("/dispatch hl.dsp.window.move", StringComparison.Ordinal));
        Assert.Equal("0xbeef", fake.ActiveAddress);
    }

    [Fact]
    public async Task PlaceAsync_FallsBackToTheHyprlangDispatcher()
    {
        var fake = new FakeHyprland { LuaAccepted = false };
        Assert.True(await LinuxHyprlandPanelPlacement.PlaceAsync(fake.Send, "DP-2", _ => Task.CompletedTask));
        Assert.Contains("/dispatch movetoworkspacesilent 4,address:0xa1", fake.Sent);
    }

    [Fact]
    public async Task PlaceAsync_SetsFullscreenWhenTheWindowIsAlreadyOnTheTarget()
    {
        var fake = new FakeHyprland { KioskMonitor = 1, Fullscreen = 0 };
        Assert.True(await LinuxHyprlandPanelPlacement.PlaceAsync(fake.Send, "DP-2", _ => Task.CompletedTask));
        Assert.DoesNotContain(fake.Sent, r => r.StartsWith("/dispatch hl.dsp.window.move", StringComparison.Ordinal));
        Assert.Contains(fake.Sent, r => r.StartsWith("/dispatch hl.dsp.window.fullscreen", StringComparison.Ordinal));
    }

    [Fact]
    public async Task PlaceAsync_GivesUpWhenNoWindowAppears()
    {
        var polls = 0;
        string? Send(string r) => r == "j/clients" ? "[]" : r == "j/monitors" ? MonitorsJson : "{}";
        Assert.False(await LinuxHyprlandPanelPlacement.PlaceAsync(Send, "DP-2", _ => { polls++; return Task.CompletedTask; }));
        Assert.True(polls > 1);
    }

    [Fact]
    public void SelectDevice_NeedsExactlyOneY70Match()
    {
        var kernel = new[] { "ILITEK    ILITEK-TOUCH" };
        Assert.Equal(("ilitek----ilitek-touch", LinuxHyprlandTouchMap.Outcome.Mapped),
            LinuxHyprlandTouchMap.SelectDevice(["a-touchscreen", "ilitek----ilitek-touch"], kernel));
        Assert.Equal(LinuxHyprlandTouchMap.Outcome.NoTouchDevice, LinuxHyprlandTouchMap.SelectDevice(["a-touchscreen"], kernel).Outcome);
        Assert.Equal(LinuxHyprlandTouchMap.Outcome.NoTouchDevice, LinuxHyprlandTouchMap.SelectDevice(["a-touchscreen"], []).Outcome);
        Assert.Equal(LinuxHyprlandTouchMap.Outcome.Ambiguous,
            LinuxHyprlandTouchMap.SelectDevice(["x-y", "x-z"], ["X-Y", "X Z"]).Outcome);
    }

    [Fact]
    public void UserConfigClaims_IgnoresCommentedLines()
    {
        const string name = "ilitek-touch";
        Assert.True(LinuxHyprlandTouchMap.UserConfigClaims("hl.device({ name = \"ilitek-touch\", output = \"DP-2\" })", name));
        Assert.True(LinuxHyprlandTouchMap.UserConfigClaims("input { touchdevice:output = DP-2 }", name));
        Assert.False(LinuxHyprlandTouchMap.UserConfigClaims("-- hl.device({ name = \"ilitek-touch\" })\n# touchdevice", name));
        Assert.False(LinuxHyprlandTouchMap.UserConfigClaims("", name));
    }

    [Fact]
    public void Apply_BindsTheY70TouchToTheMonitorWithItsTransform()
    {
        var sent = new List<string>();
        string? Send(string r)
        {
            sent.Add(r);
            return r switch
            {
                "j/monitors" => MonitorsJson,
                "j/devices" => """{"touch":[{"address":"0x1","name":"ilitek-touch"}]}""",
                _ when r.StartsWith("j/getoption", StringComparison.Ordinal) => """{"option":"x","str":"[[EMPTY]]","set":false }""",
                _ => "ok",
            };
        }
        Assert.Equal(LinuxHyprlandTouchMap.Outcome.Mapped, LinuxHyprlandTouchMap.Apply(Send, "DP-2", ["ILITEK-TOUCH"], ""));
        Assert.Contains("/eval hl.device({ name = \"ilitek-touch\", output = \"DP-2\", transform = 3 })", sent);
    }

    [Fact]
    public void Apply_LeavesAUserMappingAlone()
    {
        var sent = new List<string>();
        string? Send(string r)
        {
            sent.Add(r);
            return r switch
            {
                "j/monitors" => MonitorsJson,
                "j/devices" => """{"touch":[{"address":"0x1","name":"ilitek-touch"}]}""",
                _ => """{"option":"x","str":"DP-2","set":true }""",
            };
        }
        Assert.Equal(LinuxHyprlandTouchMap.Outcome.UserConfigured, LinuxHyprlandTouchMap.Apply(Send, "DP-2", ["ILITEK-TOUCH"], ""));
        Assert.DoesNotContain(sent, r => r.StartsWith("/eval", StringComparison.Ordinal));
        Assert.Equal(LinuxHyprlandTouchMap.Outcome.UserConfigured,
            LinuxHyprlandTouchMap.Apply(r => r == "j/monitors" ? MonitorsJson : r == "j/devices" ? """{"touch":[{"name":"ilitek-touch"}]}""" : "{}",
                "DP-2", ["ILITEK-TOUCH"], "hl.device({ name = \"ilitek-touch\" })"));
    }

    [Fact]
    public void ReadY70TouchNames_SkipsNodesWithACorsairEndpointOnTheHub()
    {
        var root = Directory.CreateTempSubdirectory("hypr-sysfs").FullName;
        try
        {
            void Node(string hubName, string eventName, string name, string vid, string pid, bool corsair)
            {
                var hub = Path.Combine(root, "devices", hubName);
                var usb = Path.Combine(hub, hubName + ".1");
                var input = Path.Combine(usb, "if0", "input", "input" + eventName);
                Directory.CreateDirectory(Path.Combine(input, "id"));
                File.WriteAllText(Path.Combine(usb, "idVendor"), vid);
                File.WriteAllText(Path.Combine(input, "name"), name);
                File.WriteAllText(Path.Combine(input, "id", "vendor"), vid);
                File.WriteAllText(Path.Combine(input, "id", "product"), pid);
                if (corsair)
                {
                    var c = Path.Combine(hub, hubName + ".2");
                    Directory.CreateDirectory(c);
                    File.WriteAllText(Path.Combine(c, "idVendor"), "1b1c");
                    File.WriteAllText(Path.Combine(c, "idProduct"), "1d0d");
                }
                var cls = Path.Combine(root, "class", "input", "event" + eventName);
                Directory.CreateDirectory(cls);
                Directory.CreateSymbolicLink(Path.Combine(cls, "device"), input);
            }
            Node("1-1", "5", "ILITEK ILITEK-TOUCH", "222a", "0001", corsair: false);
            Node("1-2", "6", "Edge Touch", "27c0", "0859", corsair: true);
            Node("1-3", "7", "Other", "046d", "c52b", corsair: false);
            Assert.Equal(["ILITEK ILITEK-TOUCH"], LinuxInputDevices.ReadY70TouchNames(root));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Theory]
    [InlineData(true, true, false, true)]
    [InlineData(true, true, true, false)]
    [InlineData(true, false, false, false)]
    [InlineData(false, true, false, false)]
    public void ShouldReconcile_FiresOnlyOnTheAppearEdgeWhileWanted(bool wanted, bool present, bool wasPresent, bool expected)
        => Assert.Equal(expected, LinuxY70DisplayWatcher.ShouldReconcile(wanted, present, wasPresent));

    [Fact]
    public void Watcher_ReconcilesOnceWhenTheDisplayAppears()
    {
        var present = false;
        var wanted = true;
        var reconciles = 0;
        var watcher = new LinuxY70DisplayWatcher(() => wanted, () => present, () => reconciles++);
        watcher.Tick();
        present = true;
        watcher.Tick();
        watcher.Tick();
        Assert.Equal(1, reconciles);
        wanted = false;
        watcher.Tick();
        wanted = true;
        watcher.Tick();
        Assert.Equal(1, reconciles);
    }
}
