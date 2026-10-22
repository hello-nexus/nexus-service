using Nexus.Service.Deck;
using Nexus.Service.Hotkeys;
using Nexus.Service.Persistence;
using Nexus.Service.Sockets;
using Xunit;

namespace Nexus.Service.Tests.Hotkeys;

public sealed class FakeHotkeyRegistrar : IHotkeyRegistrar
{
    public string Platform { get; set; } = "macos";
    public bool Supported { get; set; } = true;
    public bool ReleaseOnStop { get; set; }
    public HashSet<string> RefuseIds { get; } = new();
    public List<List<HotkeyRegistration>> Applied { get; } = new();
    /// <summary>When set, ApplyAsync waits on it, holding the reconcile that called it.</summary>
    public TaskCompletionSource? Block { get; set; }
    public event Action<string, string>? Fired;
    public event Action? NeedsReapply;

    public List<HotkeyRegistration> Last => Applied[^1];

    public async Task<IReadOnlyDictionary<string, bool>> ApplyAsync(IReadOnlyList<HotkeyRegistration> set, CancellationToken ct)
    {
        Applied.Add(set.ToList());
        IReadOnlyDictionary<string, bool> result = set.ToDictionary(r => r.Id, r => !RefuseIds.Contains(r.Id));
        if (Block is { } block)
        {
            await block.Task;
        }
        return result;
    }

    public void Press(string id, string combo) => Fired?.Invoke(id, combo);
    public void RaiseReapply() => NeedsReapply?.Invoke();
}

public sealed class SpyDeckExecutor : IDeckActionExecutor
{
    public List<(DeckAction? Action, string Serial, string LatchKey)> Calls { get; } = new();

    public Task ExecuteAsync(DeckAction? action, string serial, int keyIndex, string latchKey, CancellationToken ct)
    {
        Calls.Add((action, serial, latchKey));
        return Task.CompletedTask;
    }

    public bool IsToggleOn(DeckToggleState? state, string latchKey) => false;
    public void OpenApp() { }
}

public sealed class HotkeyServiceTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "nexus-hotkeys-" + Guid.NewGuid().ToString("N"));
    private readonly JsonConfigStore _store;
    private readonly FakeHotkeyRegistrar _registrar = new();
    private readonly SpyDeckExecutor _executor = new();
    private readonly MultiplexHub _hub = new();

    public HotkeyServiceTests()
    {
        Directory.CreateDirectory(_dir);
        _store = new JsonConfigStore(Path.Combine(_dir, "settings.json"));
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best-effort temp cleanup */ }
    }

    private HotkeyService Service(bool macos = true) => new(_store, _registrar, _executor, _hub, macos);

    private static HotkeyBinding ActionBinding(string id, string? combo, bool enabled = true) => new()
    {
        Id = id,
        Combo = combo,
        Enabled = enabled,
        Target = new HotkeyTarget { Kind = HotkeyTarget.KindAction, Action = new DeckAction { Type = "hotkey", Keys = "ctrl+c" } },
    };

    private static HotkeyBinding DeckBinding(string id, string combo, string presetId) => new()
    {
        Id = id,
        Combo = combo,
        Target = new HotkeyTarget
        {
            Kind = HotkeyTarget.KindDeck,
            PresetId = presetId,
            Tag = id,
        },
    };

    private void AddPreset(string id, params DeckSlot[] slots)
    {
        _store.Update(s =>
        {
            var page = new DeckPage();
            page.Slots.AddRange(slots);
            if (page.Slots.Count > 0)
            {
                page.Slots[0].HotkeyTag = "d1";
            }
            s.StreamDeck.Presets.Add(new DeckPreset { Id = id, Name = id, Cols = 3, Rows = 2, Deck = new DeckConfig { Pages = { page } } });
        });
    }

    private Dictionary<string, string> Statuses(HotkeyService service)
        => service.BuildResponse().Bindings.ToDictionary(b => b.Id, b => b.Status);

    // ---- Defaults -----------------------------------------------------------

    [Fact]
    public void Defaults_seed_once_with_cmd_opt_on_macos()
    {
        var service = Service(macos: true);
        service.SeedDefaults();

        var combos = _store.Load().Hotkeys.Bindings.ToDictionary(b => b.Id, b => b.Combo);
        Assert.Equal("Alt+Meta+n", combos[HotkeyDefaults.ToggleWindow]);
        Assert.Equal("Alt+Meta+l", combos[HotkeyDefaults.LightingToggle]);
        Assert.Equal("Alt+Meta+=", combos[HotkeyDefaults.BrightnessUp]);
        Assert.Equal("Alt+Meta+-", combos[HotkeyDefaults.BrightnessDown]);
        Assert.True(_store.Load().Hotkeys.DefaultsSeeded);
        Assert.All(_store.Load().Hotkeys.Bindings, b => Assert.True(b.Enabled));
    }

    [Fact]
    public void Defaults_use_ctrl_alt_on_windows()
    {
        Service(macos: false).SeedDefaults();
        var combos = _store.Load().Hotkeys.Bindings.ToDictionary(b => b.Id, b => b.Combo);
        Assert.Equal("Ctrl+Alt+n", combos[HotkeyDefaults.ToggleWindow]);
        Assert.Equal("Ctrl+Alt+=", combos[HotkeyDefaults.BrightnessUp]);
    }

    [Fact]
    public void Default_actions_are_the_new_nexus_ops_with_signed_brightness_steps()
    {
        Service().SeedDefaults();
        var byId = _store.Load().Hotkeys.Bindings.ToDictionary(b => b.Id, b => b.Target.Action!);
        Assert.All(byId.Values, a => Assert.Equal("nexus", a.Type));
        Assert.Equal("toggleWindow", byId[HotkeyDefaults.ToggleWindow].NexusAction!.Op);
        Assert.Equal("lightingPowerToggle", byId[HotkeyDefaults.LightingToggle].NexusAction!.Op);
        Assert.Equal(0.1, byId[HotkeyDefaults.BrightnessUp].NexusAction!.Value);
        Assert.Equal(-0.1, byId[HotkeyDefaults.BrightnessDown].NexusAction!.Value);
    }

    [Fact]
    public async Task A_deleted_default_stays_deleted_until_reset()
    {
        var service = Service();
        service.SeedDefaults();
        await service.DeleteAsync(HotkeyDefaults.LightingToggle);

        service.SeedDefaults();
        Assert.DoesNotContain(_store.Load().Hotkeys.Bindings, b => b.Id == HotkeyDefaults.LightingToggle);

        await service.ResetDefaultsAsync();
        Assert.Contains(_store.Load().Hotkeys.Bindings, b => b.Id == HotkeyDefaults.LightingToggle);
    }

    [Fact]
    public async Task Reset_restores_edited_defaults_and_keeps_custom_bindings()
    {
        var service = Service();
        service.SeedDefaults();
        var edited = ActionBinding(HotkeyDefaults.ToggleWindow, "Ctrl+Alt+z", enabled: false);
        await service.UpsertAsync(edited);
        await service.UpsertAsync(ActionBinding("custom:one", "Ctrl+Alt+q"));

        await service.ResetDefaultsAsync();

        var bindings = _store.Load().Hotkeys.Bindings;
        var toggle = bindings.Single(b => b.Id == HotkeyDefaults.ToggleWindow);
        Assert.Equal("Alt+Meta+n", toggle.Combo);
        Assert.True(toggle.Enabled);
        Assert.Contains(bindings, b => b.Id == "custom:one");
    }

    [Fact]
    public void Hotkeys_are_not_part_of_the_shareable_profile_categories()
    {
        // A profile switch copies only the sharing categories; Hotkeys lives at the settings root beside them.
        var live = new NexusSettings();
        live.Hotkeys.Bindings.Add(ActionBinding("custom:keep", "Ctrl+Alt+k"));
        var extract = ProfileSharing.ExtractShareable(live);
        Assert.Empty(extract.Hotkeys.Bindings);
        Assert.Single(live.Hotkeys.Bindings);
    }

    // ---- Statuses and registration ------------------------------------------

    [Fact]
    public async Task Eligible_bindings_register_and_report_active()
    {
        var service = Service();
        service.SeedDefaults();
        await service.ReconcileAsync();

        Assert.Equal(4, _registrar.Last.Count);
        Assert.All(Statuses(service).Values, status => Assert.Equal(HotkeyStatus.Active, status));
    }

    [Fact]
    public async Task Disabled_unbound_and_unparseable_bindings_do_not_register()
    {
        var service = Service();
        await service.UpsertAsync(ActionBinding("a", "Ctrl+Alt+a", enabled: false));
        await service.UpsertAsync(ActionBinding("b", null));
        _store.Update(s => s.Hotkeys.Bindings = s.Hotkeys.Bindings.Append(ActionBinding("c", "garbage")).ToList());
        await service.ReconcileAsync();

        Assert.Empty(_registrar.Last);
        var statuses = Statuses(service);
        Assert.Equal(HotkeyStatus.Disabled, statuses["a"]);
        Assert.Equal(HotkeyStatus.Unbound, statuses["b"]);
        Assert.Equal(HotkeyStatus.Unbound, statuses["c"]);
    }

    [Fact]
    public async Task Two_enabled_bindings_on_one_combo_both_conflict_and_neither_registers()
    {
        var service = Service();
        await service.UpsertAsync(ActionBinding("a", "Ctrl+Alt+a"));
        await service.UpsertAsync(ActionBinding("b", "alt+ctrl+A")); // same combo, different spelling
        await service.UpsertAsync(ActionBinding("c", "Ctrl+Alt+c"));

        var statuses = Statuses(service);
        Assert.Equal(HotkeyStatus.Conflict, statuses["a"]);
        Assert.Equal(HotkeyStatus.Conflict, statuses["b"]);
        Assert.Equal(HotkeyStatus.Active, statuses["c"]);
        Assert.Equal(new[] { "c" }, _registrar.Last.Select(r => r.Id));
    }

    [Fact]
    public async Task A_disabled_duplicate_does_not_conflict()
    {
        var service = Service();
        await service.UpsertAsync(ActionBinding("a", "Ctrl+Alt+a"));
        await service.UpsertAsync(ActionBinding("b", "Ctrl+Alt+a", enabled: false));
        var statuses = Statuses(service);
        Assert.Equal(HotkeyStatus.Active, statuses["a"]);
        Assert.Equal(HotkeyStatus.Disabled, statuses["b"]);
    }

    [Fact]
    public async Task An_os_refusal_reports_conflict_and_keeps_the_binding()
    {
        _registrar.RefuseIds.Add(HotkeyDefaults.LightingToggle);
        var service = Service();
        service.SeedDefaults();
        await service.ReconcileAsync();

        var statuses = Statuses(service);
        Assert.Equal(HotkeyStatus.Conflict, statuses[HotkeyDefaults.LightingToggle]);
        Assert.Equal(HotkeyStatus.Active, statuses[HotkeyDefaults.ToggleWindow]);
        Assert.Equal(4, statuses.Count);
    }

    [Fact]
    public async Task Unsupported_platform_reports_unsupported_and_registers_nothing()
    {
        _registrar.Supported = false;
        _registrar.Platform = "linux";
        var service = Service();
        service.SeedDefaults();
        await service.ReconcileAsync();

        var response = service.BuildResponse();
        Assert.False(response.Supported);
        Assert.Equal("linux", response.Platform);
        Assert.All(response.Bindings, b => Assert.Equal(HotkeyStatus.Unsupported, b.Status));
        Assert.Empty(_registrar.Applied);
    }

    [Fact]
    public async Task A_deck_binding_whose_preset_is_deleted_becomes_orphaned_and_unregisters()
    {
        AddPreset("p1", new DeckSlot { Action = new DeckAction { Type = "hotkey", Keys = "ctrl+a" } });
        var service = Service();
        await service.UpsertAsync(DeckBinding("d1", "Ctrl+Alt+d", "p1"));
        Assert.Equal(HotkeyStatus.Active, Statuses(service)["d1"]);
        Assert.Single(_registrar.Last);

        _store.Update(s => s.StreamDeck.Presets.Clear());
        await service.ReconcileAsync();

        Assert.Equal(HotkeyStatus.Orphaned, Statuses(service)["d1"]);
        Assert.Empty(_registrar.Last);
        Assert.Contains(_store.Load().Hotkeys.Bindings, b => b.Id == "d1");
    }

    [Fact]
    public async Task Emptying_the_targeted_slot_orphans_the_binding()
    {
        AddPreset("p1", new DeckSlot { Action = new DeckAction { Type = "hotkey", Keys = "ctrl+a" } });
        var service = Service();
        await service.UpsertAsync(DeckBinding("d1", "Ctrl+Alt+d", "p1"));

        _store.Update(s => s.StreamDeck.Presets[0].Deck.Pages[0].Slots[0] = new DeckSlot());
        await service.ReconcileAsync();

        Assert.Equal(HotkeyStatus.Orphaned, Statuses(service)["d1"]);
    }

    [Fact]
    public async Task Store_changes_reconcile_in_the_background_once_started()
    {
        AddPreset("p1", new DeckSlot { Action = new DeckAction { Type = "hotkey", Keys = "ctrl+a" } });
        var service = Service();
        await service.StartAsync(CancellationToken.None);
        try
        {
            await service.UpsertAsync(DeckBinding("d1", "Ctrl+Alt+d", "p1"));
            Assert.Contains(_registrar.Last, r => r.Id == "d1");

            _store.Update(s => s.StreamDeck.Presets.Clear());
            await WaitUntil(() => Statuses(service)["d1"] == HotkeyStatus.Orphaned && !_registrar.Last.Any(r => r.Id == "d1"));
        }
        finally
        {
            await service.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task A_registrar_reapply_request_pushes_the_set_again()
    {
        var service = Service();
        await service.StartAsync(CancellationToken.None);
        try
        {
            var before = _registrar.Applied.Count;
            _registrar.RaiseReapply();
            await WaitUntil(() => _registrar.Applied.Count > before);
            Assert.Equal(4, _registrar.Last.Count);
        }
        finally
        {
            await service.StopAsync(CancellationToken.None);
        }
    }

    // ---- Suspend --------------------------------------------------------------

    [Fact]
    public async Task Suspend_unregisters_everything_and_resume_registers_again()
    {
        var service = Service();
        service.SeedDefaults();
        await service.ReconcileAsync();

        await service.SetSuspendedAsync(true);
        Assert.True(service.BuildResponse().Suspended);
        Assert.Empty(_registrar.Last);
        // Statuses stay what they would be.
        Assert.All(Statuses(service).Values, status => Assert.Equal(HotkeyStatus.Active, status));

        await service.SetSuspendedAsync(false);
        Assert.False(service.BuildResponse().Suspended);
        Assert.Equal(4, _registrar.Last.Count);
    }

    [Fact]
    public async Task Suspend_resumes_on_its_own_after_the_timeout()
    {
        var service = Service();
        service.SuspendTimeout = TimeSpan.FromMilliseconds(80);
        service.SeedDefaults();
        await service.ReconcileAsync();

        await service.SetSuspendedAsync(true);
        Assert.True(service.BuildResponse().Suspended);
        await WaitUntil(() => !service.BuildResponse().Suspended && _registrar.Last.Count == 4);
        service.Dispose();
    }

    [Fact]
    public async Task A_press_while_suspended_runs_nothing()
    {
        var service = Service();
        await service.UpsertAsync(ActionBinding("a", "Ctrl+Alt+a"));
        await service.SetSuspendedAsync(true);
        _registrar.Press("a", "Ctrl+Alt+a");
        await Task.Delay(50);
        Assert.Empty(_executor.Calls);
        service.Dispose();
    }

    // ---- Firing ---------------------------------------------------------------

    [Fact]
    public async Task Firing_an_action_binding_runs_its_action_with_no_deck_serial()
    {
        var service = Service();
        await service.UpsertAsync(ActionBinding("a", "Ctrl+Alt+a"));

        await service.FireAsync("a");

        var call = Assert.Single(_executor.Calls);
        Assert.Equal("hotkey", call.Action!.Type);
        Assert.Equal("", call.Serial);
        Assert.Equal("hotkey:a", call.LatchKey);
    }

    [Fact]
    public async Task Firing_a_deck_binding_runs_the_slots_current_action()
    {
        AddPreset("p1", new DeckSlot { Action = new DeckAction { Type = "hotkey", Keys = "ctrl+a" } });
        var service = Service();
        await service.UpsertAsync(DeckBinding("d1", "Ctrl+Alt+d", "p1"));

        // Edit the slot after binding: the hotkey runs what is stored now, with no deck or window involved.
        _store.Update(s => s.StreamDeck.Presets[0].Deck.Pages[0].Slots[0].Action = new DeckAction { Type = "hotkey", Keys = "ctrl+z" });
        await service.FireAsync("d1");

        Assert.Equal("ctrl+z", Assert.Single(_executor.Calls).Action!.Keys);
    }

    [Fact]
    public async Task Orphaned_disabled_and_unknown_bindings_do_not_fire()
    {
        AddPreset("p1", new DeckSlot { Action = new DeckAction { Type = "hotkey", Keys = "ctrl+a" } });
        var service = Service();
        await service.UpsertAsync(DeckBinding("d1", "Ctrl+Alt+d", "p1"));
        await service.UpsertAsync(ActionBinding("off", "Ctrl+Alt+o", enabled: false));
        _store.Update(s => s.StreamDeck.Presets.Clear());

        await service.FireAsync("d1");
        await service.FireAsync("off");
        await service.FireAsync("missing");

        Assert.Empty(_executor.Calls);
    }

    // ---- Coalescing, stop, timers, stale presses -----------------------------

    [Fact]
    public async Task A_store_change_during_a_reconcile_is_not_dropped()
    {
        AddPreset("p1", new DeckSlot { Action = new DeckAction { Type = "hotkey", Keys = "ctrl+a" } });
        var service = Service();
        await service.StartAsync(CancellationToken.None);
        try
        {
            await service.UpsertAsync(DeckBinding("d1", "Ctrl+Alt+d", "p1"));
            Assert.Contains(_registrar.Last, r => r.Id == "d1");

            var gate = new TaskCompletionSource();
            _registrar.Block = gate;
            var applies = _registrar.Applied.Count;
            _store.Update(s => s.Hotkeys.Bindings = s.Hotkeys.Bindings.Append(ActionBinding("extra", "Ctrl+Alt+x")).ToList());
            await WaitUntil(() => _registrar.Applied.Count > applies);

            // The first pass is parked inside the registrar having read d1 as live; this change must queue another pass.
            _store.Update(s => s.StreamDeck.Presets.Clear());
            _registrar.Block = null;
            gate.SetResult();

            await WaitUntil(() => Statuses(service)["d1"] == HotkeyStatus.Orphaned && !_registrar.Last.Any(r => r.Id == "d1"));
        }
        finally
        {
            await service.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task Stop_releases_an_out_of_process_registrar_and_blocks_later_registration()
    {
        _registrar.ReleaseOnStop = true;
        var service = Service();
        service.SeedDefaults();
        await service.StartAsync(CancellationToken.None);
        Assert.Equal(4, _registrar.Last.Count);

        await service.StopAsync(CancellationToken.None);
        Assert.Empty(_registrar.Last);

        var applies = _registrar.Applied.Count;
        await service.UpsertAsync(ActionBinding("late", "Ctrl+Alt+q"));
        await service.SetSuspendedAsync(true);
        await service.SetSuspendedAsync(false);
        Assert.Equal(applies, _registrar.Applied.Count);
    }

    [Fact]
    public async Task Stop_skips_the_release_for_an_in_process_registrar()
    {
        _registrar.ReleaseOnStop = false;
        var service = Service();
        service.SeedDefaults();
        await service.StartAsync(CancellationToken.None);
        var applies = _registrar.Applied.Count;

        await service.StopAsync(CancellationToken.None);

        Assert.Equal(applies, _registrar.Applied.Count);
    }

    [Fact]
    public async Task A_resume_from_an_older_suspend_cannot_cancel_a_newer_one()
    {
        var service = Service();
        service.SuspendTimeout = TimeSpan.FromMinutes(5);
        service.SeedDefaults();
        await service.SetSuspendedAsync(true);
        await service.SetSuspendedAsync(true);

        await service.ResumeAsync(1);
        Assert.True(service.BuildResponse().Suspended);

        await service.ResumeAsync(2);
        Assert.False(service.BuildResponse().Suspended);
        service.Dispose();
    }

    [Fact]
    public async Task A_press_whose_combo_is_no_longer_the_bindings_is_ignored()
    {
        var service = Service();
        await service.UpsertAsync(ActionBinding("a", "Ctrl+Alt+a"));

        await service.FireAsync("a", "Ctrl+Alt+b");
        Assert.Empty(_executor.Calls);

        await service.FireAsync("a", "alt+ctrl+A");
        Assert.Single(_executor.Calls);
    }

    [Fact]
    public async Task A_keystroke_action_releases_the_triggers_modifiers_first()
    {
        var service = Service();
        HotkeyCombo? released = null;
        service.ReleaseModifiers = combo =>
        {
            released = combo;
            return Task.CompletedTask;
        };
        await service.UpsertAsync(ActionBinding("a", "Ctrl+Alt+a"));
        await service.UpsertAsync(new HotkeyBinding
        {
            Id = "b", Combo = "Ctrl+Alt+b",
            Target = new HotkeyTarget { Kind = HotkeyTarget.KindAction, Action = new DeckAction { Type = "launchApp", AppId = "x" } },
        });

        await service.FireAsync("a");
        Assert.Equal("Ctrl+Alt+a", released.ToString());
        Assert.Single(_executor.Calls);

        released = null;
        await service.FireAsync("b");
        Assert.Null(released);
        Assert.Equal(2, _executor.Calls.Count);
    }

    // ---- Validation -----------------------------------------------------------

    [Fact]
    public void Validate_canonicalizes_the_combo()
    {
        var binding = ActionBinding("ok", "shift+ctrl+alt+K");
        Assert.Null(HotkeyService.Validate(binding));
        Assert.Equal("Ctrl+Alt+Shift+k", binding.Combo);
    }

    [Theory]
    [InlineData("", "Ctrl+Alt+a")]
    [InlineData("has space", "Ctrl+Alt+a")]
    [InlineData("ok", "n")]
    [InlineData("ok", "Ctrl+Alt+nope")]
    public void Validate_rejects_bad_ids_and_combos(string id, string combo)
    {
        Assert.NotNull(HotkeyService.Validate(ActionBinding(id, combo)));
    }

    [Fact]
    public void Validate_accepts_a_null_combo_as_unbound()
    {
        var binding = ActionBinding("ok", null);
        Assert.Null(HotkeyService.Validate(binding));
        Assert.Null(binding.Combo);
    }

    [Fact]
    public void Validate_rejects_a_null_target()
    {
        var binding = ActionBinding("ok", "Ctrl+Alt+a");
        binding.Target = null!;
        Assert.NotNull(HotkeyService.Validate(binding));
    }

    [Fact]
    public void Validate_rejects_malformed_targets()
    {
        var noAction = ActionBinding("ok", "Ctrl+Alt+a");
        noAction.Target.Action = null;
        Assert.NotNull(HotkeyService.Validate(noAction));

        var unknownKind = ActionBinding("ok", "Ctrl+Alt+a");
        unknownKind.Target.Kind = "other";
        Assert.NotNull(HotkeyService.Validate(unknownKind));

        var noTag = DeckBinding("ok", "Ctrl+Alt+a", "p1");
        noTag.Target.Tag = null;
        Assert.Equal(HotkeyErrorCodes.InvalidTarget, HotkeyService.Validate(noTag)?.Code);

        var badTag = DeckBinding("ok", "Ctrl+Alt+a", "p1");
        badTag.Target.Tag = "has space";
        Assert.Equal(HotkeyErrorCodes.InvalidTarget, HotkeyService.Validate(badTag)?.Code);

        Assert.Null(HotkeyService.Validate(DeckBinding("ok", "Ctrl+Alt+a", "p1")));
    }

    [Fact]
    public async Task The_binding_cap_is_enforced()
    {
        var service = Service();
        for (var i = 0; i < HotkeyService.MaxBindings; i++)
        {
            Assert.True(await service.UpsertAsync(ActionBinding("b" + i, null)));
        }
        Assert.False(await service.UpsertAsync(ActionBinding("overflow", null)));
        // Replacing an existing binding still works at the cap.
        Assert.True(await service.UpsertAsync(ActionBinding("b0", "Ctrl+Alt+z")));
    }

    // ---- Wire ---------------------------------------------------------------

    [Fact]
    public async Task The_status_payload_serializes_to_the_documented_wire_shape()
    {
        var service = Service();
        service.SeedDefaults();
        await service.UpsertAsync(ActionBinding("custom:x", null));
        var json = System.Text.Json.JsonSerializer.Serialize(service.BuildResponse(), Nexus.Service.Serialization.AppJsonContext.Default.HotkeysResponse);
        using var doc = System.Text.Json.JsonDocument.Parse(json);
        var root = doc.RootElement;
        Assert.True(root.GetProperty("supported").GetBoolean());
        Assert.Equal("macos", root.GetProperty("platform").GetString());
        Assert.False(root.GetProperty("suspended").GetBoolean());
        var toggle = root.GetProperty("bindings").EnumerateArray().First(b => b.GetProperty("id").GetString() == HotkeyDefaults.ToggleWindow);
        Assert.Equal("Alt+Meta+n", toggle.GetProperty("combo").GetString());
        Assert.Equal("active", toggle.GetProperty("status").GetString());
        Assert.Equal("action", toggle.GetProperty("target").GetProperty("kind").GetString());
        Assert.Equal("toggleWindow", toggle.GetProperty("target").GetProperty("action").GetProperty("action").GetProperty("op").GetString());
        var unbound = root.GetProperty("bindings").EnumerateArray().First(b => b.GetProperty("id").GetString() == "custom:x");
        Assert.Equal(System.Text.Json.JsonValueKind.Null, unbound.GetProperty("combo").ValueKind);
        Assert.Equal("unbound", unbound.GetProperty("status").GetString());
    }

    private static async Task WaitUntil(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < deadline, "condition not met in time");
            await Task.Delay(20);
        }
    }
}
