using System;
using System.Collections.Generic;
using System.Linq;
using Nexus.Service.Activity;
using Nexus.Service.Models.Activity;
using Nexus.Service.FocusModes;
using Nexus.Service.Persistence;
using Xunit;

namespace Nexus.Service.Tests;

public class FocusModeStateTests
{
    private const int AlivePid = 4242;
    private const int DeadPid = 9999;

    // Liveness is injected so a test can retire a pid without a real process.
    private static (FocusModeState State, InMemoryConfigStore Store, HashSet<int> Alive) Build(
        TimeSpan? activationDelay = null, Action<FocusSettings>? configure = null)
    {
        var store = new InMemoryConfigStore();
        store.Update(s =>
        {
            foreach (var mode in s.Focus.Modes) mode.ExitGraceSeconds = 0;
            configure?.Invoke(s.Focus);
        });
        var alive = new HashSet<int> { AlivePid };
        var state = new FocusModeState(
            store, activationDelay ?? TimeSpan.Zero, g => alive.Contains(g.Pid));
        return (state, store, alive);
    }

    private static void StartGame(FocusModeState state, int pid = AlivePid)
    {
        state.NoteGameStarted("steam:1", "Test Game", pid);
        state.SweepForTests();
    }

    private static FocusModeSettings Mode(InMemoryConfigStore store, string id) =>
        store.Load().Focus.Modes.First(m => m.Id == id);

    [Fact]
    public void StaticPanelBackgroundsIsOffByDefaultAndPersists()
    {
        Assert.All(FocusModeSettings.StockModes(), m => Assert.False(m.StaticPanelBackgrounds));

        var settings = new NexusSettings();
        settings.Focus.Modes.First(m => m.Id == FocusModeSettings.GameModeId).StaticPanelBackgrounds = true;
        var json = System.Text.Json.JsonSerializer.Serialize(settings, Nexus.Service.Serialization.PersistenceJsonContext.Default.NexusSettings);
        var back = System.Text.Json.JsonSerializer.Deserialize(json, Nexus.Service.Serialization.PersistenceJsonContext.Default.NexusSettings)!;

        Assert.True(back.Focus.Modes.First(m => m.Id == FocusModeSettings.GameModeId).StaticPanelBackgrounds);
    }

    [Fact]
    public void NothingIsActiveWithNoTrigger()
    {
        var (state, _, _) = Build();
        state.SweepForTests();

        Assert.False(state.IsActive);
        Assert.Null(state.ActiveModeId);
        Assert.Equal("", state.Reason);
    }

    [Fact]
    public void StockModesAreGameThenStreaming()
    {
        var (_, store, _) = Build();
        var modes = store.Load().Focus.Modes;

        Assert.Equal(new[] { "game", "streaming" }, modes.Select(m => m.Id));
        Assert.All(modes, m => Assert.True(m.BuiltIn));
        Assert.Equal(FocusTriggers.Game, modes[0].Trigger);
        Assert.Equal(FocusTriggers.Obs, modes[1].Trigger);
    }

    [Fact]
    public void ARunningGameActivatesTheGameMode()
    {
        var (state, _, _) = Build();
        StartGame(state);

        Assert.Equal("game", state.ActiveModeId);
        Assert.Equal("auto", state.Reason);
    }

    [Fact]
    public void ObsGoingLiveActivatesTheStreamingMode()
    {
        var (state, _, _) = Build();
        state.SetObsLive(true);

        Assert.Equal("streaming", state.ActiveModeId);
        Assert.Equal("auto", state.Reason);
    }

    [Fact]
    public void ListOrderDecidesWhichModeWinsWhenBothTriggersFire()
    {
        var (state, store, _) = Build();
        StartGame(state);
        state.SetObsLive(true);
        Assert.Equal("game", state.ActiveModeId);

        // Reordering is the user's precedence control.
        store.Update(s => s.Focus.Modes = s.Focus.Modes.OrderByDescending(m => m.Id).ToList());
        state.SweepForTests();

        Assert.Equal("streaming", state.ActiveModeId);
    }

    [Fact]
    public void AProcessThatDiesInsideTheActivationDelayNeverActivates()
    {
        // A game's launcher, updater or shutdown handler lives in the same
        // install dir, so GameCatalog resolves it to the same game; one taking
        // focus after a quit must not re-arm the mode.
        var (state, _, _) = Build(activationDelay: TimeSpan.FromMinutes(5));
        state.NoteGameStarted("steam:1", "System Shock", DeadPid);
        state.SweepForTests();

        Assert.False(state.IsActive);
    }

    [Fact]
    public void ALiveProcessStillInsideTheActivationDelayHasNotActivatedYet()
    {
        var (state, _, _) = Build(activationDelay: TimeSpan.FromMinutes(5));
        state.NoteGameStarted("steam:1", "System Shock", AlivePid);
        state.SweepForTests();

        Assert.False(state.IsActive);
    }

    [Fact]
    public void TheModeEndsOnceTheGameExitsAndTheGraceHasPassed()
    {
        var (state, _, alive) = Build();
        StartGame(state);
        Assert.True(state.IsActive);

        alive.Remove(AlivePid);
        state.SweepForTests();

        Assert.False(state.IsActive);
        Assert.Empty(state.Games);
    }

    [Fact]
    public void TheModeHoldsInsideTheGraceWindow()
    {
        var (state, store, alive) = Build();
        store.Update(s => s.Focus.Modes.First(m => m.Id == "game").ExitGraceSeconds = 600);
        StartGame(state);

        alive.Remove(AlivePid);
        state.SweepForTests();

        Assert.True(state.IsActive);
        Assert.Empty(state.Games);
    }

    [Fact]
    public void AManualPickActivatesAModeWithNoTrigger()
    {
        var (state, _, _) = Build();
        state.ActivateManually("streaming");

        Assert.Equal("streaming", state.ActiveModeId);
        Assert.Equal("manual", state.Reason);
    }

    [Fact]
    public void AManualPickOutranksAFiringTrigger()
    {
        var (state, _, _) = Build();
        StartGame(state);
        state.ActivateManually("streaming");

        Assert.Equal("streaming", state.ActiveModeId);
        Assert.Equal("manual", state.Reason);
    }

    [Fact]
    public void OffHoldsAnAutoModeOutUntilItsTriggerClears()
    {
        // "Off" has to mean "not now", not "never": with the game still
        // running, re-evaluating must not immediately switch it back on.
        var (state, _, alive) = Build();
        StartGame(state);
        Assert.True(state.IsActive);

        state.TurnOff();
        Assert.False(state.IsActive);

        state.SweepForTests();
        Assert.False(state.IsActive);

        // The next game re-arms it.
        alive.Remove(AlivePid);
        state.SweepForTests();
        alive.Add(AlivePid);
        StartGame(state);
        Assert.True(state.IsActive);
    }

    [Fact]
    public void OffClearsAManualPick()
    {
        var (state, _, _) = Build();
        state.ActivateManually("game");
        Assert.True(state.IsActive);

        state.TurnOff();

        Assert.False(state.IsActive);
    }

    [Fact]
    public void AManualTriggeredModeIgnoresARunningGame()
    {
        var (state, store, _) = Build(configure: f =>
        {
            f.Modes.First(m => m.Id == "game").Trigger = FocusTriggers.Manual;
        });
        StartGame(state);

        Assert.False(state.IsActive);

        state.ActivateManually("game");
        Assert.Equal("game", state.ActiveModeId);
        Assert.Equal("manual", state.Reason);
    }

    [Fact]
    public void OffHoldsAManualPickOutWhileItsOwnTriggerIsStillFiring()
    {
        var (state, _, _) = Build();
        StartGame(state);
        state.ActivateManually("game");
        Assert.True(state.IsActive);

        state.TurnOff();

        // The game is still running, so the auto trigger would re-pick this
        // mode on the same pass unless Off suppressed it.
        Assert.False(state.IsActive);
    }

    [Fact]
    public void OffOnASecondModeKeepsTheFirstSuppressed()
    {
        var (state, _, _) = Build(configure: f =>
        {
            f.Modes.First(m => m.Id == "streaming").Trigger = FocusTriggers.Game;
        });
        StartGame(state);
        Assert.Equal("game", state.ActiveModeId);

        state.TurnOff();
        Assert.Equal("streaming", state.ActiveModeId);

        state.TurnOff();

        Assert.False(state.IsActive);
    }

    [Fact]
    public void EveryStockNameFitsTheChip()
    {
        var (_, store, _) = Build();
        Assert.All(store.Load().Focus.Modes, m => Assert.True(m.Name.Length <= 10, m.Name));
    }

    [Fact]
    public void ADeletedModeCannotStayActive()
    {
        var (state, store, _) = Build();
        state.ActivateManually("streaming");
        Assert.True(state.IsActive);

        store.Update(s => s.Focus.Modes.RemoveAll(m => m.Id == "streaming"));
        state.SweepForTests();

        Assert.False(state.IsActive);
    }

    [Fact]
    public void ActiveChangedFiresOnlyOnChanges()
    {
        var (state, _, alive) = Build();
        var seen = new List<string?>();
        state.ActiveChanged += m => seen.Add(m?.Id);

        StartGame(state);
        state.SweepForTests();
        state.SweepForTests();
        Assert.Equal(new string?[] { "game" }, seen);

        alive.Remove(AlivePid);
        state.SweepForTests();
        Assert.Equal(new string?[] { "game", null }, seen);
    }

    [Theory]
    [InlineData("manual", true)]
    [InlineData("game", true)]
    [InlineData("obs", true)]
    [InlineData("app", true)]
    [InlineData("", false)]
    [InlineData("mic", false)]
    [InlineData(null, false)]
    public void OnlyImplementedTriggersAreAccepted(string? id, bool known)
    {
        Assert.Equal(known, FocusTriggers.IsKnown(id));
    }

    private static (FocusModeState State, InMemoryConfigStore Store, List<ProcessInfo> Procs) BuildApp(
        TimeSpan? delay = null, int graceSeconds = 0, string processName = "chrome", string displayName = "Google Chrome")
    {
        var store = new InMemoryConfigStore();
        store.Update(s => s.Focus.Modes.Add(new FocusModeSettings
        {
            Id = "apps",
            Name = "Browse",
            Trigger = FocusTriggers.App,
            ExitGraceSeconds = graceSeconds,
            Apps = new List<PresetAppBinding>
            {
                new() { Id = "proc:" + processName, Name = displayName, ProcessName = processName },
            },
        }));
        var procs = new List<ProcessInfo>();
        var state = new FocusModeState(store, delay ?? TimeSpan.Zero, g => false, () => procs.ToList());
        return (state, store, procs);
    }

    [Fact]
    public void ARunningAppActivatesItsMode()
    {
        var (state, _, procs) = BuildApp();
        state.SweepForTests();
        Assert.False(state.IsActive);

        procs.Add(new ProcessInfo { Pid = 1, Name = "chrome.exe" });
        state.SweepForTests();

        Assert.Equal("apps", state.ActiveModeId);
        Assert.Equal("auto", state.Reason);
    }

    [Fact]
    public void AnyOneOfSeveralAppsFiresTheMode()
    {
        var (state, store, procs) = BuildApp();
        store.Update(s => s.Focus.Modes.First(m => m.Id == "apps").Apps.Add(
            new PresetAppBinding { Id = "proc:code", Name = "Code", ProcessName = "code" }));
        procs.Add(new ProcessInfo { Pid = 2, Name = "Code" });
        state.SweepForTests();

        Assert.Equal("apps", state.ActiveModeId);
    }

    [Fact]
    public void AnAppModeWaitsOutTheActivationDelay()
    {
        var (state, _, procs) = BuildApp(delay: TimeSpan.FromMinutes(5));
        procs.Add(new ProcessInfo { Pid = 1, Name = "chrome" });
        state.SweepForTests();

        Assert.False(state.IsActive);
    }

    [Fact]
    public void TheAppModeEndsOnceTheAppExitsAndTheGraceHasPassed()
    {
        var (state, _, procs) = BuildApp();
        procs.Add(new ProcessInfo { Pid = 1, Name = "chrome" });
        state.SweepForTests();
        Assert.True(state.IsActive);

        procs.Clear();
        state.SweepForTests();

        Assert.False(state.IsActive);
    }

    [Fact]
    public void TheAppModeHoldsInsideTheGraceWindowAfterTheAppExits()
    {
        var (state, _, procs) = BuildApp(graceSeconds: 600);
        procs.Add(new ProcessInfo { Pid = 1, Name = "chrome" });
        state.SweepForTests();

        procs.Clear();
        state.SweepForTests();

        Assert.True(state.IsActive);
    }

    [Fact]
    public void FocusingAnotherWindowDoesNotMatterToAnAppMode()
    {
        // Only the running list is consulted, so an alt-tab leaves the mode on.
        var (state, _, procs) = BuildApp();
        procs.Add(new ProcessInfo { Pid = 1, Name = "chrome" });
        procs.Add(new ProcessInfo { Pid = 2, Name = "notepad", HasWindow = true });
        state.SweepForTests();
        state.SweepForTests();

        Assert.Equal("apps", state.ActiveModeId);
    }

    [Fact]
    public void OffHoldsAnAppModeOutUntilTheAppQuits()
    {
        var (state, _, procs) = BuildApp();
        procs.Add(new ProcessInfo { Pid = 1, Name = "chrome" });
        state.SweepForTests();

        state.TurnOff();
        state.SweepForTests();
        Assert.False(state.IsActive);

        procs.Clear();
        state.SweepForTests();
        procs.Add(new ProcessInfo { Pid = 3, Name = "chrome" });
        state.SweepForTests();
        Assert.Equal("apps", state.ActiveModeId);
    }

    [Fact]
    public void ASubstringProcessOfTheDisplayNameDoesNotFireTheMode()
    {
        var (state, _, procs) = BuildApp(processName: "steamlink", displayName: "Steam Link");
        procs.Add(new ProcessInfo { Pid = 1, Name = "steam" });
        procs.Add(new ProcessInfo { Pid = 2, Name = "link" });
        state.SweepForTests();
        Assert.False(state.IsActive);

        procs.Add(new ProcessInfo { Pid = 3, Name = "SteamLink.exe" });
        state.SweepForTests();
        Assert.True(state.IsActive);
    }

    [Fact]
    public void AnAppModeWithNoAppsNeverFires()
    {
        var (state, store, procs) = BuildApp();
        store.Update(s => s.Focus.Modes.First(m => m.Id == "apps").Apps.Clear());
        procs.Add(new ProcessInfo { Pid = 1, Name = "chrome" });
        state.SweepForTests();

        Assert.False(state.IsActive);
    }

    [Fact]
    public void AppsRoundTripThroughPersistenceAndOldFilesLoadEmpty()
    {
        var settings = new NexusSettings();
        settings.Focus.Modes.Add(new FocusModeSettings
        {
            Id = "apps",
            Trigger = FocusTriggers.App,
            Apps = new List<PresetAppBinding> { new() { Id = "proc:obs64", Name = "OBS", ProcessName = "obs64" } },
        });
        var ctx = Nexus.Service.Serialization.PersistenceJsonContext.Default.NexusSettings;
        var back = System.Text.Json.JsonSerializer.Deserialize(
            System.Text.Json.JsonSerializer.Serialize(settings, ctx), ctx)!;

        var app = Assert.Single(back.Focus.Modes.First(m => m.Id == "apps").Apps);
        Assert.Equal(("proc:obs64", "OBS", "obs64"), (app.Id, app.Name, app.ProcessName));

        var old = System.Text.Json.JsonSerializer.Deserialize(
            "{\"focus\":{\"modes\":[{\"id\":\"x\",\"name\":\"X\",\"trigger\":\"game\"}]}}", ctx)!;
        Assert.Empty(old.Focus.Modes.Single().Apps);
    }

    private sealed class FakeShortcuts : IShortcutsProvider
    {
        public IReadOnlyList<Shortcut> GetAll() => Array.Empty<Shortcut>();
        public Shortcut? GetById(string targetId) => null;
        public byte[] GetIcon(string targetId) => Array.Empty<byte>();
        public bool Launch(string targetId) => false;
        public string ResolveProcessName(string targetId) => targetId == "lnk:known" ? "Known.exe" : "";
    }

    [Fact]
    public void PatchResolutionDropsAppsThatResolveToNothing()
    {
        var store = new InMemoryConfigStore();
        var picked = new List<Nexus.Service.Models.Focus.FocusAppDto>
        {
            new() { Id = "proc:Steam.exe", Name = "Steam", ProcessName = "ignored" },
            new() { Id = "lnk:known", Name = "Known", ProcessName = "" },
            new() { Id = "lnk:clientsent", Name = "Sent", ProcessName = "Sent.EXE" },
            new() { Id = "lnk:unknown", Name = "Nothing", ProcessName = "" },
            new() { Id = "proc:Steam.exe", Name = "Dupe", ProcessName = "" },
            new() { Id = "", Name = "Blank", ProcessName = "x" },
        };

        var resolved = Nexus.Service.Routes.FocusRoutes.ResolveApps(picked, store, new FakeShortcuts());

        Assert.Equal(
            new[] { ("proc:Steam.exe", "steam"), ("lnk:known", "known"), ("lnk:clientsent", "sent") },
            resolved.Select(a => (a.Id, a.ProcessName)));
    }
}
