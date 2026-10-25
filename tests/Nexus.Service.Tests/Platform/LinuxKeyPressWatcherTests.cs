using System.Buffers.Binary;
using System.Runtime.InteropServices;
using Nexus.Service.Lighting.KeyReactive;
using Nexus.Service.Persistence;
using Nexus.Service.Platform.Linux;

namespace Nexus.Service.Tests.Platform;

/// <summary>
/// Keyboard selection from sysfs capabilities, evdev parsing, and the open/close
/// lifecycle of the Linux key source. The lifecycle tests read real pipes in place
/// of /dev/input nodes (POSIX only).
/// </summary>
public sealed class LinuxKeyPressWatcherTests : IDisposable
{
    private const int KeyA = 30;
    private readonly string _root = Path.Combine(Path.GetTempPath(), "nexus-keypress-" + Guid.NewGuid().ToString("N"));
    private readonly List<int> _fds = new();

    public void Dispose()
    {
        foreach (var fd in _fds) close(fd);
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }

    // Capability words as the kernel prints them: hex, most significant first.
    private const string FullKeyboardKeys = "ffffffffffffffff ffffffffffffffff ffffffffffffffff fffffffffffffffe";
    private const string MediaOnlyKeys = "0 0 0 e000000000000 0";

    private string AddInput(string eventName, string? ev, string? keys, string? name)
    {
        var dir = Path.Combine(_root, "sys", eventName, "device");
        Directory.CreateDirectory(Path.Combine(dir, "capabilities"));
        if (ev is not null) File.WriteAllText(Path.Combine(dir, "capabilities", "ev"), ev + "\n");
        if (keys is not null) File.WriteAllText(Path.Combine(dir, "capabilities", "key"), keys + "\n");
        if (name is not null) File.WriteAllText(Path.Combine(dir, "name"), name + "\n");
        return Path.Combine(_root, "dev", eventName);
    }

    private string Sys => Path.Combine(_root, "sys");
    private string Dev => Path.Combine(_root, "dev");

    private static byte[] KeyEvent(int code, int value, ushort type = 1)
    {
        var b = new byte[LinuxKeyPressWatcher.EventSize];
        BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(16), type);
        BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(18), (ushort)code);
        BinaryPrimitives.WriteInt32LittleEndian(b.AsSpan(20), value);
        return b;
    }

    [Theory]
    [InlineData("1 2 4", 0, false)]
    [InlineData("2", 1, true)]
    [InlineData("120013", 1, true)]
    [InlineData("120013", 0, true)]
    [InlineData("120013", 2, false)]
    [InlineData("1 0", 64, true)]
    [InlineData("1 0", 0, false)]
    [InlineData("", 1, false)]
    [InlineData(null, 1, false)]
    [InlineData("zz", 1, false)]
    public void HasBit_ReadsKernelBitmaps(string? bitmap, int bit, bool expected)
        => Assert.Equal(expected, LinuxKeyPressWatcher.HasBit(bitmap, bit));

    [Fact]
    public void FindKeyboards_PicksKeyboardsAndMediaInterfacesOnly()
    {
        // A letter keyboard, its media interface, a mouse (buttons only), a power button, an accelerometer (no EV_KEY), and a non-event node.
        AddInput("event3", "120013", FullKeyboardKeys, "Some Keyboard");
        AddInput("event4", "12001f", MediaOnlyKeys, "Some Keyboard Consumer Control");
        AddInput("event5", "17", "1f0000 0 0 0 0", "Some Mouse");
        AddInput("event6", "3", "100000 0 0 0 0", "Power Button");
        AddInput("event7", "9", null, "Accelerometer");
        AddInput("mouse0", "120013", FullKeyboardKeys, "Legacy Mouse Node");
        AddInput("event", "120013", FullKeyboardKeys, "No Number");
        AddInput("event10", "120013", FullKeyboardKeys, null);

        var found = LinuxKeyPressWatcher.FindKeyboards(Sys, Dev);

        Assert.Equal(
            new[] { "event10", "event3", "event4" },
            found.Select(k => Path.GetFileName(k.Node)).ToArray());
        Assert.All(found, k => Assert.StartsWith(Dev, k.Node));
        Assert.Equal("Some Keyboard", found.Single(k => k.Node.EndsWith("event3")).Name);
        Assert.Equal("event10", found.Single(k => k.Node.EndsWith("event10")).Name);
    }

    [Fact]
    public void FindKeyboards_MissingSysfsIsEmpty()
        => Assert.Empty(LinuxKeyPressWatcher.FindKeyboards(Path.Combine(_root, "absent"), Dev));

    [Fact]
    public void ParseKeyDowns_KeepsOnlyKeyPressesNotReleaseRepeatOrOtherTypes()
    {
        var data = new List<byte>();
        data.AddRange(KeyEvent(KeyA, 1));
        data.AddRange(KeyEvent(KeyA, 0));
        data.AddRange(KeyEvent(KeyA, 2));
        data.AddRange(KeyEvent(0, 0, type: 0));
        data.AddRange(KeyEvent(KeyA, 1, type: 4));
        data.AddRange(KeyEvent(48, 1));
        data.AddRange(new byte[10]);
        var codes = new List<int>();

        LinuxKeyPressWatcher.ParseKeyDowns(data.ToArray(), codes);

        Assert.Equal(new[] { KeyA, 48 }, codes);
    }

    // ---- lifecycle (real pipes standing in for event nodes)

    private static async Task<bool> WaitFor(Func<bool> condition, int timeoutMs = 5000)
    {
        var until = Environment.TickCount64 + timeoutMs;
        while (Environment.TickCount64 < until)
        {
            if (condition()) return true;
            await Task.Delay(10);
        }
        return condition();
    }

    private sealed class Rig
    {
        public readonly Dictionary<string, int> WriteEnds = new();
        public readonly List<string> Presses = new();
        public int Opens;
        public volatile bool Allowed = true;
    }

    private LinuxKeyPressWatcher Build(Rig rig, KeyReactiveOverlay? overlay = null, Func<string, int>? open = null)
        => new(overlay, null, key => { lock (rig.Presses) rig.Presses.Add(key); }, () => rig.Allowed, Sys, Dev,
            open ?? (node =>
            {
                Interlocked.Increment(ref rig.Opens);
                var fds = new int[2];
                Assert.Equal(0, pipe(fds));
                lock (_fds) { _fds.Add(fds[1]); }
                rig.WriteEnds[node] = fds[1];
                return fds[0];
            }));

    private static void Send(int fd, params byte[][] events)
    {
        foreach (var e in events)
        {
            Assert.Equal(e.Length, (int)write(fd, e, (nuint)e.Length));
        }
    }

    private static int PressCount(Rig rig) { lock (rig.Presses) return rig.Presses.Count; }

    [Fact]
    public async Task NeverArmed_OpensNothing()
    {
        if (OperatingSystem.IsWindows()) return;
        AddInput("event3", "120013", FullKeyboardKeys, "Keyboard");
        var rig = new Rig();
        using var watcher = Build(rig);

        await Task.Delay(700);

        Assert.Equal(0, rig.Opens);
        Assert.Equal(0, watcher.OpenCount);
        Assert.False(watcher.Watching);
    }

    [Fact]
    public async Task Armed_ReadsPressesOnlyWhileArmedAndClosesWhenDisarmed()
    {
        if (OperatingSystem.IsWindows()) return;
        var node = AddInput("event3", "120013", FullKeyboardKeys, "Keyboard");
        var rig = new Rig();
        using var watcher = Build(rig);

        watcher.SetArmed(true);
        Assert.True(await WaitFor(() => watcher.OpenCount == 1));
        Send(rig.WriteEnds[node], KeyEvent(KeyA, 1), KeyEvent(KeyA, 0));
        Assert.True(await WaitFor(() => PressCount(rig) == 1));
        Assert.Equal("A", rig.Presses[0]);

        watcher.SetArmed(false);
        Assert.True(await WaitFor(() => watcher.OpenCount == 0));
        Assert.Equal(1, rig.Opens);
        Assert.False(watcher.Watching);
    }

    [Fact]
    public async Task Overlay_ArmingFollowsAKeyboardEnablingReactions()
    {
        if (OperatingSystem.IsWindows()) return;
        AddInput("event3", "120013", FullKeyboardKeys, "Keyboard");
        var overlay = new KeyReactiveOverlay();
        var rig = new Rig();
        using var watcher = Build(rig, overlay);
        await watcher.StartAsync(CancellationToken.None);

        await Task.Delay(700);
        Assert.Equal(0, rig.Opens);

        overlay.Set("openrgb:kb", new KeyReaction { Enabled = true });
        Assert.True(await WaitFor(() => watcher.OpenCount == 1));

        overlay.Set("openrgb:kb", new KeyReaction { Enabled = false });
        Assert.True(await WaitFor(() => watcher.OpenCount == 0));

        overlay.Set("openrgb:kb", new KeyReaction { Enabled = true });
        Assert.True(await WaitFor(() => watcher.OpenCount == 1));
        await watcher.StopAsync(CancellationToken.None);
        Assert.True(await WaitFor(() => watcher.OpenCount == 0));
    }

    [Fact]
    public async Task Overlay_KeyboardWithItsOwnKeySourceDoesNotArm()
    {
        if (OperatingSystem.IsWindows()) return;
        AddInput("event3", "120013", FullKeyboardKeys, "Keyboard");
        var overlay = new KeyReactiveOverlay();
        overlay.SetHardwareKeySource("keeb", present: true);
        var rig = new Rig();
        using var watcher = Build(rig, overlay);
        await watcher.StartAsync(CancellationToken.None);

        overlay.Set("keeb", new KeyReaction { Enabled = true });
        await Task.Delay(700);

        Assert.Equal(0, rig.Opens);
    }

    [Fact]
    public async Task BlockedGate_KeepsNodesClosedAndDeliversNothing_ThenOpensWhenAllowed()
    {
        if (OperatingSystem.IsWindows()) return;
        var node = AddInput("event3", "120013", FullKeyboardKeys, "Keyboard");
        var rig = new Rig { Allowed = false };
        using var watcher = Build(rig);

        watcher.SetArmed(true);
        await Task.Delay(1200);
        Assert.Equal(0, rig.Opens);

        rig.Allowed = true;
        Assert.True(await WaitFor(() => watcher.OpenCount == 1));
        Send(rig.WriteEnds[node], KeyEvent(KeyA, 1));
        Assert.True(await WaitFor(() => PressCount(rig) == 1));

        rig.Allowed = false;
        Assert.True(await WaitFor(() => watcher.OpenCount == 0));
        Assert.Equal(1, PressCount(rig));
    }

    [Fact]
    public async Task UnreadableNode_IsTriedOnceNotEveryRescan()
    {
        if (OperatingSystem.IsWindows()) return;
        AddInput("event3", "120013", FullKeyboardKeys, "Keyboard");
        var rig = new Rig();
        var attempts = 0;
        using var watcher = Build(rig, open: _ => { Interlocked.Increment(ref attempts); return -1; });

        watcher.SetArmed(true);
        await Task.Delay((int)LinuxKeyPressWatcher.RescanMs * 2 + 500);

        Assert.Equal(1, attempts);
        Assert.Equal(0, watcher.OpenCount);
    }

    [Fact]
    public async Task HotplugAndRemoval_OpenNewKeyboardAndDropAGoneOne()
    {
        if (OperatingSystem.IsWindows()) return;
        var first = AddInput("event3", "120013", FullKeyboardKeys, "First");
        var rig = new Rig();
        using var watcher = Build(rig);
        watcher.SetArmed(true);
        Assert.True(await WaitFor(() => watcher.OpenCount == 1));

        var second = AddInput("event4", "120013", FullKeyboardKeys, "Second");
        Assert.True(await WaitFor(() => watcher.OpenCount == 2, timeoutMs: 6000));

        // Closing the write end hangs up the read end, like an unplugged board.
        close(rig.WriteEnds[first]);
        Assert.True(await WaitFor(() => watcher.OpenCount == 1));
        Send(rig.WriteEnds[second], KeyEvent(KeyA, 1));
        Assert.True(await WaitFor(() => PressCount(rig) == 1));
    }

    [Fact]
    public async Task SameKeyOnTwoNodesWithinTheDuplicateWindowCountsOnce()
    {
        if (OperatingSystem.IsWindows()) return;
        var a = AddInput("event3", "120013", FullKeyboardKeys, "Keyboard");
        var b = AddInput("event4", "12001f", FullKeyboardKeys, "Keyboard Aux");
        var rig = new Rig();
        using var watcher = Build(rig);
        watcher.SetArmed(true);
        Assert.True(await WaitFor(() => watcher.OpenCount == 2));

        Send(rig.WriteEnds[a], KeyEvent(KeyA, 1));
        Send(rig.WriteEnds[b], KeyEvent(KeyA, 1));

        Assert.True(await WaitFor(() => PressCount(rig) >= 1));
        await Task.Delay(200);
        Assert.Equal(1, PressCount(rig));
    }

    [DllImport("libc", SetLastError = true)]
    private static extern int pipe(int[] fds);

    [DllImport("libc", SetLastError = true)]
    private static extern nint write(int fd, byte[] buffer, nuint count);

    [DllImport("libc", SetLastError = true)]
    private static extern int close(int fd);
}
