using Nexus.Service.Lighting.Engine;
using Nexus.Service.Lighting.KeyReactive;
using Nexus.Service.Peripherals.Hyte.Keeb;
using Nexus.Service.Persistence;

namespace Nexus.Service.Tests.Lighting;

public class KeyReactiveTests
{
    private const string Id = "openrgb:kb";

    // A 10 x 4 grid of named keys: rows "1234567890", "QWERTYUIOP", "ASDFGHJKL;", "ZXCVBNM,./".
    private static readonly string[] Rows = { "1234567890", "QWERTYUIOP", "ASDFGHJKL;", "ZXCVBNM,./" };
    private const int Cols = 10;

    private static (float[] U, float[] V, string?[] Names) Grid(bool named = true)
    {
        var n = Rows.Length * Cols;
        var u = new float[n];
        var v = new float[n];
        var names = new string?[n];
        for (var r = 0; r < Rows.Length; r++)
        {
            for (var c = 0; c < Cols; c++)
            {
                var i = r * Cols + c;
                u[i] = c / (float)(Cols - 1);
                v[i] = r / (float)(Rows.Length - 1);
                names[i] = named ? "Key: " + Rows[r][c] : null;
            }
        }
        return (u, v, names);
    }

    private static int Led(char key)
    {
        for (var r = 0; r < Rows.Length; r++)
        {
            var c = Rows[r].IndexOf(key);
            if (c >= 0) return r * Cols + c;
        }
        throw new ArgumentException(key.ToString());
    }

    private static KeyboardGeometry Geo(bool named = true)
    {
        var (u, v, names) = Grid(named);
        return KeyboardGeometry.Build(u, v, names, null);
    }

    private static KeyReaction Cfg(string effect, string background = KeyReactionCatalog.BackgroundDark) =>
        KeyReactionCatalog.Sanitize(new KeyReaction
        {
            Enabled = true, Effect = effect, Color = "#ff0000", Background = background,
        });

    private static byte[] Frame(KeyReactionRenderer renderer, KeyboardGeometry geo, KeyReaction cfg, double now, byte baseLevel = 0)
    {
        var rgb = Enumerable.Repeat(baseLevel, geo.LedCount * 3).ToArray();
        renderer.Render(geo, cfg, now);
        renderer.Composite(rgb, geo.LedCount, cfg.Background);
        return rgb;
    }

    private static int Red(byte[] rgb, int led) => rgb[led * 3];

    private static DeviceFrame Keyboard()
    {
        var (u, v, names) = Grid();
        // Pad to the per-key threshold with unnamed, disabled LEDs.
        var n = Math.Max(u.Length, KeyReactionCatalog.MinKeyLeds);
        var pu = new float[n];
        var pv = new float[n];
        var pn = new string?[n];
        var disabled = new bool[n];
        Array.Copy(u, pu, u.Length);
        Array.Copy(v, pv, v.Length);
        Array.Copy(names, pn, names.Length);
        for (var i = u.Length; i < n; i++) disabled[i] = true;
        return new DeviceFrame(0, Id, n) { Archetype = "keyboard", LedU = pu, LedV = pv, LedKeys = pn, LedDisabled = disabled };
    }

    // ── Key names ──

    [Theory]
    [InlineData(0x1E, false, 0, "A")]
    [InlineData(0x02, false, 0, "1")]
    [InlineData(0x0B, false, 0, "0")]
    [InlineData(0x48, false, 0, "Number Pad 8")]
    [InlineData(0x48, true, 0, "Up Arrow")]
    [InlineData(0x1C, true, 0, "Number Pad Enter")]
    [InlineData(0x1D, true, 0, "Right Control")]
    [InlineData(0x56, false, 0, "\\ (ISO)")]
    [InlineData(0x1D, false, 0x13, "Pause/Break")]
    [InlineData(0x45, false, 0x90, "Num Lock")]
    [InlineData(0x57, false, 0, "F11")]
    [InlineData(0x64, false, 0, "F13")]
    public void Scan_codes_map_to_openrgb_key_names(int make, bool e0, int vkey, string expected)
    {
        Assert.Equal(expected, KeyNames.FromScanCode(make, e0, vkey));
    }

    [Fact]
    public void Fake_shift_from_an_e0_sequence_is_not_a_key()
    {
        Assert.Null(KeyNames.FromScanCode(0x2A, e0: true));
    }

    [Fact]
    public void Led_names_drop_the_openrgb_prefix()
    {
        Assert.Equal("Left Shift", KeyNames.Normalize("Key: Left Shift"));
        Assert.Equal("Logo", KeyNames.Normalize("Logo"));
        Assert.Null(KeyNames.Normalize(""));
        Assert.Null(KeyNames.Normalize("Key: "));
    }

    [Fact]
    public void Every_keeb_wire_slot_name_is_a_name_a_scan_code_produces()
    {
        foreach (var map in KeebKeyMap.All)
        {
            var names = KeebKeyNames.For(map);
            Assert.Equal(map.LedCount, names.Length);
            foreach (var name in names)
            {
                if (name is null || name == "Right Fn") continue;
                Assert.True(KeyNames.IsKnown(name), name);
            }
        }
    }

    [Fact]
    public void Keeb_ansi_names_every_typing_key()
    {
        var names = KeebKeyNames.For(KeebKeyMap.Ansi);
        Assert.Contains("Escape", names);
        Assert.Contains("Print Screen", names);
        Assert.Contains("\\", names);
        Assert.DoesNotContain("\\ (ISO)", names);
        Assert.Contains("\\ (ISO)", KeebKeyNames.For(KeebKeyMap.Iso));
    }

    // ── Geometry ──

    [Fact]
    public void Geometry_measures_positions_in_key_pitches()
    {
        var geo = Geo();
        Assert.Equal(1f, geo.X[Led('2')] - geo.X[Led('1')], 3);
        Assert.Equal(1f, geo.Y[Led('Q')] - geo.Y[Led('1')], 3);
        Assert.Equal(9f, geo.MaxX - geo.MinX, 3);
        Assert.Equal(3f, geo.MaxY - geo.MinY, 3);
    }

    [Fact]
    public void Named_board_resolves_by_name_and_rejects_keys_it_lacks()
    {
        var geo = Geo();
        Assert.Equal(Led('A'), geo.Resolve("A"));
        Assert.Equal(Led(';'), geo.Resolve(";"));
        Assert.Equal(-1, geo.Resolve("Escape"));
    }

    [Fact]
    public void Aliases_find_the_iso_twin_of_a_key()
    {
        var u = new[] { 0f, 1f };
        var v = new[] { 0f, 0f };
        var geo = KeyboardGeometry.Build(u, v, new[] { "Key: Enter (ISO)", "Key: < >" }, null);
        Assert.Equal(0, geo.Resolve("Enter"));
        Assert.Equal(1, geo.Resolve("\\ (ISO)"));
    }

    [Fact]
    public void Unnamed_board_falls_back_to_the_nearest_position()
    {
        var geo = Geo(named: false);
        // Q is the second LED of the second row on the nominal board too.
        var led = geo.Resolve("Q");
        Assert.True(led >= 0);
        Assert.Equal(geo.Y[Led('Q')], geo.Y[led], 3);
    }

    [Fact]
    public void Disabled_leds_neither_resolve_nor_react()
    {
        var (u, v, names) = Grid();
        var disabled = new bool[u.Length];
        disabled[Led('A')] = true;
        var geo = KeyboardGeometry.Build(u, v, names, disabled);
        Assert.Equal(-1, geo.Resolve("A"));
        Assert.False(geo.IsActive(Led('A')));
    }

    // ── Effects ──

    [Fact]
    public void Fade_lights_only_the_pressed_key_and_dies_out()
    {
        var geo = Geo();
        var cfg = Cfg(KeyReactionCatalog.Fade);
        var renderer = new KeyReactionRenderer(seed: 1);
        renderer.Press(geo, Led('G'), cfg, 0);

        var start = Frame(renderer, geo, cfg, 0);
        Assert.Equal(255, Red(start, Led('G')));
        Assert.Equal(0, Red(start, Led('F')));
        Assert.Equal(0, Red(start, Led('T')));

        var mid = Frame(renderer, geo, cfg, 300);
        Assert.InRange(Red(mid, Led('G')), 1, 254);

        Frame(renderer, geo, cfg, 5000);
        Assert.True(renderer.Idle);
    }

    [Fact]
    public void Speed_scales_the_effect_clock()
    {
        var geo = Geo();
        var slow = Cfg(KeyReactionCatalog.Fade);
        var fast = KeyReactionCatalog.Sanitize(new KeyReaction { Enabled = true, Effect = KeyReactionCatalog.Fade, Color = "#ff0000", Speed = 3f, Background = KeyReactionCatalog.BackgroundDark });
        var a = new KeyReactionRenderer(seed: 1);
        var b = new KeyReactionRenderer(seed: 1);
        a.Press(geo, Led('G'), slow, 0);
        b.Press(geo, Led('G'), fast, 0);
        Assert.True(Red(Frame(a, geo, slow, 200), Led('G')) > Red(Frame(b, geo, fast, 200), Led('G')));
    }

    [Fact]
    public void Row_sweep_stays_in_the_pressed_row()
    {
        var geo = Geo();
        var cfg = Cfg(KeyReactionCatalog.RowSweep);
        var renderer = new KeyReactionRenderer(seed: 1);
        renderer.Press(geo, Led('A'), cfg, 0);

        // A quarter second in, the head is several keys out along the row.
        var frame = Frame(renderer, geo, cfg, 150);
        var litInRow = Rows[2].Count(k => Red(frame, Led(k)) > 0);
        Assert.True(litInRow >= 2);
        foreach (var k in Rows[0] + Rows[1] + Rows[3]) Assert.Equal(0, Red(frame, Led(k)));
    }

    [Fact]
    public void Column_sweep_stays_in_the_pressed_column()
    {
        var geo = Geo();
        var cfg = Cfg(KeyReactionCatalog.ColumnSweep);
        var renderer = new KeyReactionRenderer(seed: 1);
        renderer.Press(geo, Led('W'), cfg, 0);
        var frame = Frame(renderer, geo, cfg, 60);
        Assert.True(Red(frame, Led('S')) > 0 || Red(frame, Led('2')) > 0);
        foreach (var k in "QERTYUIOP") Assert.Equal(0, Red(frame, Led(k)));
    }

    [Fact]
    public void Ripple_ring_moves_outward()
    {
        var geo = Geo();
        var cfg = Cfg(KeyReactionCatalog.Ripple);
        var renderer = new KeyReactionRenderer(seed: 1);
        renderer.Press(geo, Led('A'), cfg, 0);
        // Once the ring has travelled a few keys, the origin is dark and a key that far out is lit.
        var frame = Frame(renderer, geo, cfg, 220);
        Assert.Equal(0, Red(frame, Led('A')));
        Assert.True(Red(frame, Led('G')) > 0);
    }

    [Fact]
    public void Rainbow_ripple_paints_more_than_one_hue()
    {
        var geo = Geo();
        var cfg = KeyReactionCatalog.Sanitize(new KeyReaction { Enabled = true, Effect = KeyReactionCatalog.Ripple, ColorMode = KeyReactionCatalog.ColorRainbow, Background = KeyReactionCatalog.BackgroundDark, Size = 3f });
        var renderer = new KeyReactionRenderer(seed: 1);
        renderer.Press(geo, Led('A'), cfg, 0);
        var frame = Frame(renderer, geo, cfg, 200);
        var colours = new HashSet<(byte, byte, byte)>();
        for (var i = 0; i < geo.LedCount; i++)
        {
            if (frame[i * 3] + frame[i * 3 + 1] + frame[i * 3 + 2] > 200) colours.Add((frame[i * 3], frame[i * 3 + 1], frame[i * 3 + 2]));
        }
        Assert.True(colours.Count >= 3);
    }

    [Fact]
    public void Heatmap_builds_with_presses_and_cools_off()
    {
        var geo = Geo();
        var cfg = Cfg(KeyReactionCatalog.Heatmap);
        var renderer = new KeyReactionRenderer(seed: 1);
        renderer.Press(geo, Led('E'), cfg, 0);
        var once = Frame(renderer, geo, cfg, 0);
        for (var i = 0; i < 4; i++) renderer.Press(geo, Led('E'), cfg, 0);
        var hot = Frame(renderer, geo, cfg, 0);
        // Hotter means further along the palette: red rises from the blue end.
        Assert.True(Red(hot, Led('E')) > Red(once, Led('E')));
        Assert.True(hot[Led('R') * 3 + 2] > 0 || Red(hot, Led('R')) > 0);

        Frame(renderer, geo, cfg, 60_000);
        Assert.True(renderer.Idle);
    }

    [Theory]
    [InlineData(KeyReactionCatalog.Crosshair)]
    [InlineData(KeyReactionCatalog.Starburst)]
    [InlineData(KeyReactionCatalog.Sparks)]
    [InlineData(KeyReactionCatalog.Lightning)]
    [InlineData(KeyReactionCatalog.Trace)]
    public void Every_effect_draws_then_finishes(string effect)
    {
        var geo = Geo();
        var cfg = Cfg(effect);
        var renderer = new KeyReactionRenderer(seed: 7);
        renderer.Press(geo, Led('G'), cfg, 0);
        renderer.Press(geo, Led('K'), cfg, 50);
        var drew = false;
        for (var t = 50; t <= 400; t += 33)
        {
            if (Frame(renderer, geo, cfg, t).Any(b => b > 0)) drew = true;
        }
        Assert.True(drew);
        Frame(renderer, geo, cfg, 10_000);
        Assert.True(renderer.Idle);
    }

    [Fact]
    public void Trace_links_consecutive_presses()
    {
        var geo = Geo();
        var cfg = Cfg(KeyReactionCatalog.Trace);
        var renderer = new KeyReactionRenderer(seed: 1);
        renderer.Press(geo, Led('A'), cfg, 0);
        renderer.Press(geo, Led('G'), cfg, 100);
        var frame = Frame(renderer, geo, cfg, 300);
        // Keys between the two presses carry the trail.
        Assert.True(Red(frame, Led('D')) > 0);
    }

    // ── Compositing ──

    [Fact]
    public void Backgrounds_treat_the_rest_of_the_board_as_specified()
    {
        var geo = Geo();
        foreach (var (background, expectIdle) in new[]
        {
            (KeyReactionCatalog.BackgroundEffect, 100),
            (KeyReactionCatalog.BackgroundDim, 18),
            (KeyReactionCatalog.BackgroundDark, 0),
            (KeyReactionCatalog.BackgroundReveal, 0),
        })
        {
            var cfg = Cfg(KeyReactionCatalog.Fade, background);
            var renderer = new KeyReactionRenderer(seed: 1);
            renderer.Press(geo, Led('G'), cfg, 0);
            var frame = Frame(renderer, geo, cfg, 0, baseLevel: 100);
            Assert.Equal(expectIdle, frame[Led('Z') * 3 + 1]);
            if (background == KeyReactionCatalog.BackgroundReveal)
            {
                // Reveal shows the effect under the reaction, not the reaction colour.
                Assert.Equal(new byte[] { 100, 100, 100 }, frame[(Led('G') * 3)..(Led('G') * 3 + 3)]);
            }
            else
            {
                Assert.Equal(255, Red(frame, Led('G')));
            }
        }
    }

    [Fact]
    public void Sanitize_clamps_and_replaces_unknown_values()
    {
        var clean = KeyReactionCatalog.Sanitize(new KeyReaction
        {
            Effect = "nope", ColorMode = "plaid", Color = "red", Speed = 99f, Size = float.NaN, Background = "x",
        });
        Assert.Equal(KeyReactionCatalog.Ripple, clean.Effect);
        Assert.Equal(KeyReactionCatalog.ColorCustom, clean.ColorMode);
        Assert.Equal(KeyReactionCatalog.DefaultColor, clean.Color);
        Assert.Equal(KeyReactionCatalog.MaxSpeed, clean.Speed);
        Assert.Equal(1f, clean.Size);
        Assert.Equal(KeyReactionCatalog.BackgroundEffect, clean.Background);
    }

    // ── Overlay ──

    [Fact]
    public void Overlay_arms_with_the_first_enabled_board_and_persists()
    {
        var store = new InMemoryConfigStore();
        var overlay = new KeyReactiveOverlay(store);
        var states = new List<bool>();
        overlay.ArmedChanged += states.Add;

        overlay.Set(Id, new KeyReaction { Enabled = true, Effect = KeyReactionCatalog.Sparks });
        Assert.True(overlay.Armed);
        Assert.Equal(KeyReactionCatalog.Sparks, store.Load().Lighting.KeyReactions[Id].Effect);
        Assert.Equal(KeyReactionCatalog.Sparks, new KeyReactiveOverlay(store).Get(Id).Effect);

        overlay.Set(Id, new KeyReaction { Enabled = false });
        Assert.False(overlay.Armed);
        Assert.Equal(new[] { true, false }, states);
    }

    [Fact]
    public void Overlay_paints_a_real_press_on_an_enabled_keyboard()
    {
        var overlay = new KeyReactiveOverlay();
        overlay.Set(Id, Cfg(KeyReactionCatalog.Fade, KeyReactionCatalog.BackgroundEffect));
        var board = Keyboard();
        board.Fill(0, 40, 0);

        overlay.PressKey("H");
        Assert.True(overlay.Apply(new[] { board }, null, Environment.TickCount64));

        // Press and paint read the wall clock separately, so the fade may have started.
        var paint = board.PaintBuffer;
        Assert.InRange(paint[Led('H') * 3], 200, 255);
        Assert.Equal(40, paint[Led('J') * 3 + 1]);
    }

    [Fact]
    public void Overlay_ignores_presses_while_disarmed_and_skips_masked_devices()
    {
        var overlay = new KeyReactiveOverlay();
        var board = Keyboard();
        overlay.PressKey("H");
        Assert.False(overlay.Apply(new[] { board }, null, Environment.TickCount64));

        overlay.Set(Id, Cfg(KeyReactionCatalog.Fade, KeyReactionCatalog.BackgroundEffect));
        overlay.PressKey("H");
        Assert.False(overlay.Apply(new[] { board }, new[] { true }, Environment.TickCount64));
    }

    [Fact]
    public void Simulated_press_can_target_an_led()
    {
        var overlay = new KeyReactiveOverlay();
        overlay.Set(Id, Cfg(KeyReactionCatalog.Fade));
        var board = Keyboard();
        overlay.PressOn(Id, key: null, led: Led('M'));
        overlay.Apply(new[] { board }, null, Environment.TickCount64);
        Assert.InRange(board.PaintBuffer[Led('M') * 3], 200, 255);
    }

    [Fact]
    public void Boards_under_the_per_key_threshold_are_not_keyboards()
    {
        var zoned = new DeviceFrame(0, "ibp:km7", 27)
        {
            Archetype = "keyboard", LedU = new float[27], LedV = new float[27],
        };
        Assert.Empty(KeyReactiveOverlay.Keyboards(new[] { zoned }));
        Assert.Single(KeyReactiveOverlay.Keyboards(new[] { Keyboard() }));
    }

    [Fact]
    public async Task Engine_paints_reactions_over_the_running_effect()
    {
        var overlay = new KeyReactiveOverlay();
        overlay.Set(Id, Cfg(KeyReactionCatalog.Fade, KeyReactionCatalog.BackgroundDark));
        var board = Keyboard();
        using var engine = new LightingEngine { KeyReactive = overlay, FrameIntervalMs = 10 };
        engine.UpdateDevices(new[] { board });
        var tcs = new TaskCompletionSource<byte[]>(TaskCreationOptions.RunContinuationsAsynchronously);
        var pressed = false;
        engine.OnFrame += _ =>
        {
            if (!pressed) { pressed = true; overlay.PressKey("Q"); return; }
            var leds = board.LedBytes.ToArray();
            if (leds[Led('Q') * 3] > 0) tcs.TrySetResult(leds);
        };
        engine.SetEffect(new Nexus.Service.Lighting.Engine.Effects.BlackEffect());
        var done = await Task.WhenAny(tcs.Task, Task.Delay(3000));
        Assert.Same(tcs.Task, done);
        var frame = await tcs.Task;
        Assert.Equal(0, frame[Led('W') * 3]);
    }

    // ── Device-keyed config, hardware key sources, standalone paint ──

    private static DeviceFrame KeyboardCard(string cardId, string deviceId)
    {
        var dev = Keyboard();
        var card = new DeviceFrame(1, cardId, dev.LedCount)
        {
            Archetype = "keyboard", LedU = dev.LedU, LedV = dev.LedV, LedKeys = dev.LedKeys, LedDisabled = dev.LedDisabled, DeviceId = deviceId,
        };
        return card;
    }

    [Fact]
    public void Cards_of_one_device_share_its_config()
    {
        var overlay = new KeyReactiveOverlay();
        overlay.Set("keeb:SER", Cfg(KeyReactionCatalog.Fade));
        var a = KeyboardCard("keeb:SER:z0", "keeb:SER");
        var b = KeyboardCard("keeb:SER:z1", "keeb:SER");
        overlay.PressKey("H");
        Assert.True(overlay.Apply(new[] { a, b }, null, Environment.TickCount64));
        Assert.True(a.PaintBuffer[Led('H') * 3] > 200);
        Assert.True(b.PaintBuffer[Led('H') * 3] > 200);
        Assert.Equal("keeb:SER", KeyReactiveOverlay.Keyboards(new[] { a })[0].DeviceId);
    }

    [Fact]
    public void A_device_with_its_own_key_source_ignores_os_presses_and_takes_its_own()
    {
        var overlay = new KeyReactiveOverlay();
        overlay.Set("keeb:SER", Cfg(KeyReactionCatalog.Fade));
        overlay.Set(Id, Cfg(KeyReactionCatalog.Fade));
        overlay.SetHardwareKeySource("keeb:SER", true);
        var keeb = KeyboardCard("keeb:SER:keys", "keeb:SER");
        var other = Keyboard();

        overlay.PressKey("H");
        overlay.Apply(new[] { keeb, other }, null, Environment.TickCount64);
        Assert.Equal(0, keeb.PaintBuffer[Led('H') * 3]);
        Assert.True(other.PaintBuffer[Led('H') * 3] > 200);

        overlay.PressFromDevice("keeb:SER", "J");
        overlay.Apply(new[] { keeb, other }, null, Environment.TickCount64);
        Assert.True(keeb.PaintBuffer[Led('J') * 3] > 200);
        Assert.Equal(0, other.PaintBuffer[Led('J') * 3]);
    }

    [Fact]
    public void The_os_source_is_armed_only_for_boards_without_their_own_keys()
    {
        var overlay = new KeyReactiveOverlay();
        var states = new List<bool>();
        overlay.ArmedChanged += states.Add;
        overlay.SetHardwareKeySource("keeb:SER", true);
        overlay.Set("keeb:SER", Cfg(KeyReactionCatalog.Fade));
        Assert.False(overlay.Armed);

        // The keeb's reader dropping hands it back to OS keystrokes.
        overlay.SetHardwareKeySource("keeb:SER", false);
        Assert.True(overlay.Armed);
        Assert.Equal(new[] { true }, states);
    }

    [Fact]
    public void Standalone_paint_draws_reactions_over_black_for_the_caller_to_publish()
    {
        var overlay = new KeyReactiveOverlay();
        overlay.Set("keeb:SER", Cfg(KeyReactionCatalog.Fade, KeyReactionCatalog.BackgroundReveal));
        var card = KeyboardCard("keeb:SER:keys", "keeb:SER");
        card.Fill(0, 90, 0);

        overlay.PressFromDevice("keeb:SER", "G");
        Assert.True(overlay.PaintStandalone(new[] { card }, Environment.TickCount64));

        var paint = card.PaintBuffer;
        // Reveal over black would show nothing, so the reaction colour paints instead.
        Assert.True(paint[Led('G') * 3] > 200);
        Assert.Equal(0, paint[Led('F') * 3 + 1]);
        Assert.False(overlay.PaintStandalone(new[] { Keyboard() }, Environment.TickCount64));
    }

    [Fact]
    public void Lock_painting_holds_locked_leds_on_a_writer_owned_frame()
    {
        var locks = new Nexus.Service.Lighting.LedColorLockTracker();
        locks.Set("keeb:SER:keys", new[] { Led('A') }, "#00ff00");
        var card = KeyboardCard("keeb:SER:keys", "keeb:SER");
        locks.PaintLocks(card);
        Assert.Equal(255, card.PaintBuffer[Led('A') * 3 + 1]);
        Assert.Equal(0, card.PaintBuffer[Led('S') * 3 + 1]);
    }

    [Fact]
    public void Presses_older_than_any_reaction_are_dropped_on_drain()
    {
        var overlay = new KeyReactiveOverlay();
        overlay.Set(Id, Cfg(KeyReactionCatalog.Fade, KeyReactionCatalog.BackgroundEffect));
        var board = Keyboard();
        // Nothing drains while no effect runs; a burst far past the cap stays bounded.
        for (var i = 0; i < 1000; i++) overlay.PressOn(Id, "H");
        overlay.PressOn(Id, "A");
        // Drained much later, every press is stale: nothing lights.
        Assert.False(overlay.Apply(new[] { board }, null, Environment.TickCount64 + 10_000));
        Assert.Equal(0, board.PaintBuffer[Led('A') * 3]);
    }

    [Theory]
    [InlineData(0, 0, "Escape")]
    [InlineData(3, 1, "A")]
    [InlineData(5, 16, "Right Arrow")]
    [InlineData(3, 12, "\\")]
    [InlineData(4, 1, "\\ (ISO)")]
    [InlineData(0, 14, "Print Screen")]
    public void Keeb_matrix_callbacks_map_to_key_names(int row, int column, string expected)
    {
        Assert.Equal(expected, KeebKeyNames.FromMatrix(row, column));
    }

    [Fact]
    public void Keeb_media_and_out_of_range_matrix_cells_have_no_name()
    {
        Assert.Null(KeebKeyNames.FromMatrix(3, 77));
        Assert.Null(KeebKeyNames.FromMatrix(-1, 0));
        // Stop: a media key with no LED name.
        Assert.Null(KeebKeyNames.FromMatrix(3, 14));
    }

    [Theory]
    [InlineData("SingleKey", KeyReactionCatalog.Fade, KeyReactionCatalog.ColorCustom)]
    [InlineData("HorizontalLine", KeyReactionCatalog.RowSweep, KeyReactionCatalog.ColorCustom)]
    [InlineData("VerticalLine", KeyReactionCatalog.ColumnSweep, KeyReactionCatalog.ColorCustom)]
    [InlineData("Ripple", KeyReactionCatalog.Ripple, KeyReactionCatalog.ColorRainbow)]
    public void Legacy_keeb_modes_migrate_to_their_shared_effects(string mode, string effect, string colorMode)
    {
        var s = new NexusSettings();
        s.Keeb.FirmwareLighting.KeyReactive = true;
        s.Keeb.FirmwareLighting.KeyReactiveMode = mode;
        s.Keeb.FirmwareLighting.KeyReactiveMask = true;
        s.Keeb.FirmwareLighting.KeyReactiveColor = new RgbaColor { R = 0x12, G = 0x34, B = 0x56 };

        Assert.True(KeebLegacyReactiveMigration.Pending(s));
        KeebLegacyReactiveMigration.Apply(s, "keeb:SER");

        var cfg = s.Lighting.KeyReactions["keeb:SER"];
        Assert.True(cfg.Enabled);
        Assert.Equal(effect, cfg.Effect);
        Assert.Equal(colorMode, cfg.ColorMode);
        Assert.Equal("#123456", cfg.Color);
        Assert.Equal(KeyReactionCatalog.BackgroundReveal, cfg.Background);
        Assert.False(s.Keeb.FirmwareLighting.KeyReactive);
        Assert.False(KeebLegacyReactiveMigration.Pending(s));
    }

    [Fact]
    public void Legacy_migration_never_overwrites_a_shared_config()
    {
        var s = new NexusSettings();
        s.Lighting.KeyReactions["keeb:SER"] = new KeyReaction { Enabled = true, Effect = KeyReactionCatalog.Lightning };
        s.Keeb.FirmwareLighting.KeyReactive = true;
        KeebLegacyReactiveMigration.Apply(s, "keeb:SER");
        Assert.Equal(KeyReactionCatalog.Lightning, s.Lighting.KeyReactions["keeb:SER"].Effect);
        Assert.False(KeebLegacyReactiveMigration.Pending(s));
    }

    [Fact]
    public void Untouched_legacy_settings_do_not_migrate()
    {
        var s = new NexusSettings();
        Assert.False(KeebLegacyReactiveMigration.Pending(s));
        KeebLegacyReactiveMigration.Apply(s, "keeb:SER");
        Assert.Empty(s.Lighting.KeyReactions);
    }

    [Fact]
    public void Preview_is_deterministic_and_sized_to_the_board()
    {
        var geo = Geo();
        var cfg = Cfg(KeyReactionCatalog.Sparks, KeyReactionCatalog.BackgroundEffect);
        var a = KeyReactionPreview.Render(geo, cfg);
        var b = KeyReactionPreview.Render(geo, cfg);
        Assert.Equal(a.Frames, b.Frames);
        Assert.Equal(geo.LedCount, a.X.Count);
        Assert.Equal(a.FrameCount * geo.LedCount * 3, Convert.FromBase64String(a.Frames).Length);
        Assert.Equal(9f, a.Width, 3);
    }
}
