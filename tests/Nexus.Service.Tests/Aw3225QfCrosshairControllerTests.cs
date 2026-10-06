using Nexus.Service.Models.Displays;
using Nexus.Service.Persistence;
using Nexus.Service.Platform.Displays;

namespace Nexus.Service.Tests;

public sealed class Aw3225QfCrosshairControllerTests
{
    [Theory]
    [InlineData(0, 2, 0, 0xCC04)]
    [InlineData(5, 3, 0, 0xCC56)]
    [InlineData(11, 5, 2, 0xCEBA)]
    public async Task Enable_UsesGen1HeaderDoubleWriteAndDelays(int type, int color, int mask, int packed)
    {
        var provider = new FakeDisplays();
        var store = new MemoryStore();
        var controller = Create(provider, store);

        var result = await controller.ApplyAsync(new() { Enabled = true, Type = type, Color = color, MaskControl = mask });

        Assert.Equal(new[] { "EC:0", "wait:300", $"ED:{packed}", "wait:250", $"ED:{packed}", "wait:300", "EC:6" }, provider.Trace);
        Assert.True(result.Enabled);
        Assert.Equal("", result.Error);
        Assert.Equal(type, store.Load().Devices.Aw3225QfCrosshairs["monitor1"].Type);
        Assert.All(provider.ReadCodes, code => Assert.Equal(0xEC, code));
    }

    [Theory]
    [InlineData(-1, 2, 0)]
    [InlineData(12, 2, 0)]
    [InlineData(0, -1, 0)]
    [InlineData(0, 6, 0)]
    [InlineData(0, 2, -1)]
    [InlineData(0, 2, 3)]
    public async Task InvalidSettings_NeverWrite(int type, int color, int mask)
    {
        var provider = new FakeDisplays();
        var store = new MemoryStore();
        var result = await Create(provider, store).ApplyAsync(new() { Enabled = true, Type = type, Color = color, MaskControl = mask });
        Assert.NotEmpty(result.Error);
        Assert.Empty(provider.Trace);
        Assert.Empty(store.Load().Devices.Aw3225QfCrosshairs);
    }

    [Fact]
    public async Task FailedAdjustment_RetriesWithoutEnablingOrSaving()
    {
        var provider = new FakeDisplays { FailWriteCode = 0xED };
        var store = new MemoryStore();
        var result = await Create(provider, store).ApplyAsync(new() { Enabled = true, Type = 5 });
        Assert.NotEmpty(result.Error);
        Assert.Equal(5, provider.Trace.Count(t => t.StartsWith("ED:")));
        Assert.DoesNotContain("EC:6", provider.Trace);
        Assert.Empty(store.Load().Devices.Aw3225QfCrosshairs);
    }

    [Fact]
    public async Task TransientReadAndWriteFailures_Recover()
    {
        var provider = new FakeDisplays { FailWritesRemaining = 1, FailReadsRemaining = 1 };
        var result = await Create(provider, new()).ApplyAsync(new() { Enabled = true });
        Assert.True(result.Enabled);
        Assert.Equal(2, provider.Trace.Count(t => t == "EC:0"));
        Assert.Equal(2, provider.ReadCodes.Count);
    }

    [Fact]
    public async Task Disable_DoesNotSwitchOffAnotherAlienVisionEngine()
    {
        var provider = new FakeDisplays { Engine = 0x1003 };
        var result = await Create(provider, new()).ApplyAsync(new() { Enabled = false });
        Assert.False(result.Enabled);
        Assert.Equal(3, result.ActiveEngine);
        Assert.Empty(provider.Trace);
    }

    [Fact]
    public async Task DiscoveryCache_RechecksOptOutBeforeEveryOperation()
    {
        var provider = new FakeDisplays();
        var store = new MemoryStore();
        long now = 0;
        var controller = Create(provider, store, clock: () => now);
        Assert.Equal("monitor1", controller.FindDisplayId());
        Assert.Equal("monitor1", controller.FindDisplayId());
        Assert.Equal(1, provider.Enumerations);

        store.Update(s => s.Devices.DdcDisabledDisplays.Add("monitor1"));
        var read = await controller.GetStatusAsync();
        var write = await controller.ApplyAsync(new() { Enabled = true });
        Assert.True(read.Connected);
        Assert.NotEmpty(read.Error);
        Assert.NotEmpty(write.Error);
        Assert.Empty(provider.ReadCodes);
        Assert.Empty(provider.Trace);

        now = 5001;
        controller.FindDisplayId();
        Assert.Contains("monitor1", provider.Excluded!);
        provider.Displays.Clear();
        now = 10002;
        Assert.Null(controller.FindDisplayId());
    }

    [Fact]
    public async Task OtherModel_AndUnsupportedHost_NeverUseDdc()
    {
        var provider = new FakeDisplays();
        provider.Displays[0].PhysicalDescription = "Alienware AW2725QF";
        Assert.False((await Create(provider, new()).GetStatusAsync()).Connected);
        Assert.Empty(provider.ReadCodes);
        var unsupported = new Aw3225QfCrosshairController(provider, new MemoryStore(), (_, _) => Task.CompletedTask, () => false, () => 0);
        var enumerations = provider.Enumerations;
        Assert.Null(unsupported.FindDisplayId());
        Assert.Equal(enumerations, provider.Enumerations);
    }

    [Fact]
    public async Task ConcurrentRequests_DoNotInterleaveTransactions()
    {
        var provider = new FakeDisplays();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var firstDelay = true;
        var controller = Create(provider, new(), async (ms, _) =>
        {
            if (ms == 300 && firstDelay)
            {
                firstDelay = false;
                entered.SetResult();
                await release.Task;
            }
        });
        var first = controller.ApplyAsync(new() { Enabled = true, Type = 0 });
        await entered.Task;
        var second = controller.ApplyAsync(new() { Enabled = true, Type = 5, Color = 3 });
        Assert.Equal(new[] { "EC:0" }, provider.Trace);
        release.SetResult();
        await Task.WhenAll(first, second);
        Assert.Equal(new[] { "EC:0", "ED:52228", "ED:52228", "EC:6", "EC:0", "ED:52310", "ED:52310", "EC:6" }, provider.Trace);
    }

    [Fact]
    public async Task BrowserCancellation_AfterFirstWrite_StillFinishesTransaction()
    {
        var provider = new FakeDisplays();
        using var cts = new CancellationTokenSource();
        var controller = Create(provider, new(), (_, _) => { cts.Cancel(); return Task.CompletedTask; });
        var result = await controller.ApplyAsync(new() { Enabled = true }, cts.Token);
        Assert.True(result.Enabled);
        Assert.Equal("EC:6", provider.Trace.Last());
    }

    [Fact]
    public async Task SavedSelection_IsScopedToDisplayAndLegacySelectionMigrates()
    {
        var provider = new FakeDisplays();
        var store = new MemoryStore();
        store.Load().Devices.Aw3225QfCrosshair = new() { Type = 5, Color = 3 };
        long now = 0;
        var controller = Create(provider, store, clock: () => now);
        Assert.Equal(5, (await controller.GetStatusAsync()).Config.Type);
        await controller.ApplyAsync(new() { Enabled = true, Type = 5, Color = 3 });
        Assert.Null(store.Load().Devices.Aw3225QfCrosshair);
        provider.Displays[0].Id = "monitor2";
        now = 5001;
        Assert.Equal(0, (await controller.GetStatusAsync()).Config.Type);
        await controller.ApplyAsync(new() { Enabled = false, Type = 1 });
        Assert.Equal(5, store.Load().Devices.Aw3225QfCrosshairs["monitor1"].Type);
        Assert.Equal(1, store.Load().Devices.Aw3225QfCrosshairs["monitor2"].Type);
    }

    private static Aw3225QfCrosshairController Create(FakeDisplays provider, MemoryStore store,
        Func<int, CancellationToken, Task>? delay = null, Func<long>? clock = null)
        => new(provider, store, delay ?? ((ms, _) => { provider.Trace.Add($"wait:{ms}"); return Task.CompletedTask; }), () => true, clock ?? (() => 0));

    private sealed class MemoryStore : IConfigStore
    {
        private readonly NexusSettings _settings = new();
        public string SettingsPath => ":memory:";
        public NexusSettings Load() => _settings;
        public void Update(Action<NexusSettings> mutate) { mutate(_settings); OnChanged?.Invoke(); }
        public void Reload() { }
        public void FlushNow() { }
        public event Action? OnChanged;
    }

    private sealed class FakeDisplays : IDisplayBrightnessProvider
    {
        public List<DisplayDto> Displays { get; } = new() { new() { Id = "monitor1", PhysicalDescription = "Alienware AW3225QF" } };
        public List<string> Trace { get; } = new();
        public List<byte> ReadCodes { get; } = new();
        public int Engine { get; set; } = 0x1006;
        public byte? FailWriteCode { get; set; }
        public int FailWritesRemaining { get; set; }
        public int FailReadsRemaining { get; set; }
        public int Enumerations { get; private set; }
        public IReadOnlyCollection<string>? Excluded { get; private set; }
        public string Hint => "";
        public IReadOnlyList<DisplayDto> Enumerate(IReadOnlyCollection<string>? excludedIds = null)
        { Enumerations++; Excluded = excludedIds; return Displays; }
        public DisplayVcpDto? GetVcp(string id, byte code)
        {
            ReadCodes.Add(code);
            if (FailReadsRemaining-- > 0) return null;
            return new() { Id = id, Code = code, Value = Engine };
        }
        public bool SetVcp(string id, byte code, int value)
        {
            Trace.Add($"{code:X2}:{value}");
            if (FailWriteCode == code || FailWritesRemaining-- > 0) return false;
            if (code == 0xEC) Engine = value;
            return true;
        }
        public int? GetBrightness(string id) => null;
        public DisplayBrightnessDto SetBrightness(string id, int percent) => new();
        public DisplayBrightnessWritePolicy GetBrightnessWritePolicy(string id) => new();
        public string? FindDisplayIdByHardwareName(IReadOnlyList<string> fragments) => null;
    }
}
