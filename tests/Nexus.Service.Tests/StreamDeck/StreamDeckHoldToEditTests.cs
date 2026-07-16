using System;
using System.IO;
using System.Threading.Tasks;
using Nexus.Service.Deck;
using Nexus.Service.Devices;
using Nexus.Service.Devices.Detection;
using Nexus.Service.Peripherals.StreamDeck;
using Nexus.Service.Persistence;
using Nexus.Service.Rendering;
using Nexus.Service.Sockets;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using Xunit;

namespace Nexus.Service.Tests.StreamDeck;

/// <summary>
/// Covers the blank-key hold-to-edit path: a press on an empty (or
/// past-the-configured) key starts a fill ring, and a hold beyond the
/// threshold fires the open-editor intent (pending edit + OpenApp) instead of
/// dispatching an action. Uses the simulated surface and a ManualTimeProvider
/// (see StreamDeckSleepAfterTests) so the hold clock is injected, never slept.
/// </summary>
public sealed class StreamDeckHoldToEditTests : IDisposable
{
    private static readonly StreamDeckModel Mini = StreamDeckModels.ByProductId(0x0063)!;

    private readonly string _imageCacheDir = Path.Combine(Path.GetTempPath(), "nexus-streamdeck-hold-test-" + Guid.NewGuid().ToString("N")[..8]);
    private readonly InMemoryConfigStore _store = new();
    private readonly FakeDeckActionExecutor _executor = new();
    private readonly FakeSensorProvider _sensors = new();
    private readonly ManualTimeProvider _clock = new(DateTimeOffset.UtcNow);
    private readonly StreamDeckImageCache _imageCache;
    private readonly SimulatedStreamDeckSurface _simulated;
    private readonly StreamDeckConnectionWorker _worker;

    public StreamDeckHoldToEditTests()
    {
        _simulated = new SimulatedStreamDeckSurface(Mini, "sim-0001");
        var presence = new HardwarePresence(new FixedUsbEnumerator());
        var gate = new DeviceControlGate(_store);
        gate.SetEnabled("streamdeck", true);
        _imageCache = new StreamDeckImageCache(_imageCacheDir);
        _worker = new StreamDeckConnectionWorker(
            new FakeWorkerHidEnumerator(), presence, gate, _store, _executor, _imageCache, new MultiplexHub(), _sensors,
            _simulated, _clock);
    }

    public void Dispose()
    {
        _worker.Dispose();
        try { Directory.Delete(_imageCacheDir, recursive: true); } catch { /* best effort */ }
    }

    private void ConnectWith(DeckConfig config, int brightness = 80)
    {
        _store.Update(s => s.StreamDeck.Decks["sim-0001"] = new PhysicalDeckSettings { Brightness = brightness, Deck = config });
        _worker.Tick();
    }

    private static DeckConfig OneEmptySlotPage() =>
        new() { Pages = { new DeckPage { Slots = { new DeckSlot() } } } };

    [Fact]
    public async Task Hold_BlankKey_PastThreshold_FiresPendingEditAndOpensApp()
    {
        ConnectWith(OneEmptySlotPage());

        _simulated.Poke(0, true);
        _worker.Tick();
        Assert.NotNull(_simulated.PeekKeyImage(0)); // fill ring's first frame is showing
        Assert.False(_worker.TryGetPendingEdit(out _)); // not yet fired

        _clock.Advance(TimeSpan.FromSeconds(1));
        _worker.AnimateHolds();

        Assert.True(_worker.TryGetPendingEdit(out var edit));
        Assert.Equal("sim-0001", edit.Serial);
        Assert.Equal(0, edit.Page);
        Assert.Equal(0, edit.SlotIndex);
        Assert.Empty(edit.FolderPath);

        Assert.NotNull(_worker.LastHoldFireTask);
        await _worker.LastHoldFireTask!;
        Assert.Equal(1, _executor.OpenAppCount);
        Assert.Empty(_executor.Calls); // a blank key never dispatches an action
    }

    [Fact]
    public void Hold_BlankKey_ReleasedBeforeThreshold_DoesNotFire()
    {
        ConnectWith(OneEmptySlotPage());

        _simulated.Poke(0, true);
        _worker.Tick();
        _clock.Advance(TimeSpan.FromMilliseconds(200));
        _worker.AnimateHolds();

        _simulated.Poke(0, false);
        _worker.Tick();

        _clock.Advance(TimeSpan.FromSeconds(1));
        _worker.AnimateHolds();

        Assert.False(_worker.TryGetPendingEdit(out _));
        Assert.Equal(0, _executor.OpenAppCount);
    }

    [Fact]
    public void Hold_MultipleBlankKeys_EachAnimatesAndClearsIndependently()
    {
        // An empty page: every physical key is blank.
        ConnectWith(new DeckConfig { Pages = { new DeckPage() } });

        _simulated.Poke(0, true);
        _worker.Tick();
        _simulated.Poke(1, true);
        _worker.Tick();
        Assert.True(_worker.HasActiveHold("sim-0001", 0));
        Assert.True(_worker.HasActiveHold("sim-0001", 1));
        Assert.NotNull(_simulated.PeekKeyImage(0));
        Assert.NotNull(_simulated.PeekKeyImage(1));

        // Releasing one key clears only its ring; the other keeps animating.
        _simulated.Poke(0, false);
        _worker.Tick();
        Assert.False(_worker.HasActiveHold("sim-0001", 0));
        Assert.Null(_simulated.PeekKeyImage(0));
        Assert.True(_worker.HasActiveHold("sim-0001", 1));

        _simulated.Poke(1, false);
        _worker.Tick();
        Assert.False(_worker.HasActiveHold("sim-0001", 1));
        Assert.Null(_simulated.PeekKeyImage(1));
    }

    [Fact]
    public async Task Hold_ConfiguredActionKey_DispatchesInsteadOfHolding()
    {
        var action = new DeckAction { Type = "openUrl", Url = "https://example.com" };
        ConnectWith(new DeckConfig { Pages = { new DeckPage { Slots = { new DeckSlot { Action = action } } } } });

        _simulated.Poke(0, true);
        _worker.Tick();
        if (_worker.LastDispatchTask is not null)
        {
            await _worker.LastDispatchTask;
        }

        _clock.Advance(TimeSpan.FromSeconds(1));
        _worker.AnimateHolds();

        Assert.False(_worker.TryGetPendingEdit(out _));
        Assert.Equal(0, _executor.OpenAppCount);
        Assert.Single(_executor.Calls);
    }

    [Fact]
    public void Hold_ColorOnlyKey_DoesNotStartHold()
    {
        // A color-only decorative key (no action) is not "blank off": pressing
        // it must not start the ring, so it keeps its fill instead of being
        // stranded on the ring frame.
        ConnectWith(new DeckConfig { Pages = { new DeckPage { Slots = { new DeckSlot { Color = "#ff0000" } } } } });

        _simulated.Poke(0, true);
        _worker.Tick();
        Assert.False(_worker.HasActiveHold("sim-0001", 0));

        _clock.Advance(TimeSpan.FromSeconds(1));
        _worker.AnimateHolds();
        Assert.False(_worker.TryGetPendingEdit(out _));
    }

    [Fact]
    public void Hold_KeyPastConfiguredSlots_FiresForThatSlotIndex()
    {
        // An empty page: every physical key is blank (past the 0 configured
        // slots), so a hold on key 3 targets slot index 3 in the editor.
        ConnectWith(new DeckConfig { Pages = { new DeckPage() } });

        _simulated.Poke(3, true);
        _worker.Tick();
        _clock.Advance(TimeSpan.FromSeconds(1));
        _worker.AnimateHolds();

        Assert.True(_worker.TryGetPendingEdit(out var edit));
        Assert.Equal(3, edit.SlotIndex);
    }

    [Fact]
    public void PushCurrentView_UnassignedKey_RendersOffDespiteUploadedImage()
    {
        // An empty, colorless slot with a stale uploaded fill (what the editor
        // used to push for a blank key) must still render off (black), not grey.
        var bytes = new byte[] { 9, 8, 7, 6 };
        var hash = StreamDeckImageCache.Hash(bytes);
        _imageCache.Store("sim-0001", hash, bytes);
        _store.Update(s => s.StreamDeck.Decks["sim-0001"] = new PhysicalDeckSettings
        {
            Brightness = 80,
            Deck = new DeckConfig { Pages = { new DeckPage { Slots = { new DeckSlot() } } } },
            ImageRefs = { ["0.0/0"] = hash },
        });

        _worker.Tick();

        Assert.Null(_simulated.PeekKeyImage(0));
    }

    [Fact]
    public void PushCurrentView_ColoredKey_KeepsUploadedFill()
    {
        // A color-only slot (no action) is a decorative key, not "off": it keeps
        // its uploaded fill.
        var bytes = new byte[] { 9, 8, 7, 6 };
        var hash = StreamDeckImageCache.Hash(bytes);
        _imageCache.Store("sim-0001", hash, bytes);
        _store.Update(s => s.StreamDeck.Decks["sim-0001"] = new PhysicalDeckSettings
        {
            Brightness = 80,
            Deck = new DeckConfig { Pages = { new DeckPage { Slots = { new DeckSlot { Color = "#ff0000" } } } } },
            ImageRefs = { ["0.0/0"] = hash },
        });

        _worker.Tick();

        Assert.Equal(bytes, _simulated.PeekKeyImage(0));
    }

    [Fact]
    public void PendingEdit_AgesOutAfterTtl()
    {
        ConnectWith(OneEmptySlotPage());
        _simulated.Poke(0, true);
        _worker.Tick();
        _clock.Advance(TimeSpan.FromSeconds(1));
        _worker.AnimateHolds();
        Assert.True(_worker.TryGetPendingEdit(out _));

        _clock.Advance(TimeSpan.FromMinutes(1));
        Assert.False(_worker.TryGetPendingEdit(out _));
    }
}

/// <summary>The hold fill ring grows its accent arc with the hold fraction.</summary>
public sealed class DeckHoldPromptRendererTests
{
    private static int AccentPixels(Image<Rgba32> image)
    {
        var count = 0;
        image.ProcessPixelRows(accessor =>
        {
            for (var y = 0; y < accessor.Height; y++)
            {
                var row = accessor.GetRowSpan(y);
                for (var x = 0; x < row.Length; x++)
                {
                    // The accent fill has a high blue and a low red; the black
                    // background and faint track ring do not, so this counts
                    // only the filled arc.
                    if (row[x].B >= 200 && row[x].R <= 160)
                    {
                        count++;
                    }
                }
            }
        });
        return count;
    }

    [Fact]
    public void Render_FullHold_HasMoreAccentThanStart()
    {
        using var start = DeckHoldPromptRenderer.Render(0f, 80);
        using var full = DeckHoldPromptRenderer.Render(1f, 80);

        Assert.Equal(80, start.Width);
        Assert.Equal(80, start.Height);
        Assert.True(AccentPixels(full) > AccentPixels(start));
    }
}
