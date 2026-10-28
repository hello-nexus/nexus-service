using System.Collections.Generic;
using System.Linq;
using Nexus.Service.Models.Displays;
using Nexus.Service.Models.Panel;
using Nexus.Service.Panel;
using Nexus.Service.Platform.Displays;
using Xunit;

namespace Nexus.Service.Tests.Panel;

public class PanelDisplayRelinkTests
{
    private static DisplayFingerprint Fp(long serial = 0, string? text = null, string mfg = "DEL", string product = "41B7")
        => new() { Manufacturer = mfg, Product = product, Serial = serial, SerialText = text };

    private static RelinkRecord Rec(string id, string displayId, DisplayFingerprint? fp, IReadOnlyList<string>? siblings = null) => new(id, displayId, fp, siblings);

    private static RelinkDisplay Disp(string id, DisplayFingerprint? fp) => new(id, fp);

    [Fact]
    public void Unique_serial_match_moves_the_record()
    {
        var plan = PanelDisplayRelink.Plan(
            new[] { Rec("p1", "DEL41B7-old", Fp(1234)) },
            new[] { Disp("DEL41B7-new", Fp(1234)) });

        var move = Assert.Single(plan.Moves);
        Assert.Equal(("p1", "DEL41B7-old", "DEL41B7-new"), (move.RecordId, move.OldDisplayId, move.NewDisplayId));
    }

    [Fact]
    public void Serial_text_participates_in_the_match()
    {
        var plan = PanelDisplayRelink.Plan(
            new[] { Rec("p1", "old", Fp(0, "ABC123")) },
            new[] { Disp("new-a", Fp(0, "ZZZ999")), Disp("new-b", Fp(0, "ABC123")) });

        Assert.Equal("new-b", Assert.Single(plan.Moves).NewDisplayId);
    }

    [Fact]
    public void Strong_vs_strong_serial_mismatch_is_a_different_monitor()
    {
        var plan = PanelDisplayRelink.Plan(
            new[] { Rec("p1", "old", Fp(1111)) },
            new[] { Disp("new", Fp(2222)) });

        Assert.Empty(plan.Moves);
    }

    [Fact]
    public void Strong_orphan_with_a_weak_candidate_is_a_weak_move()
    {
        var plan = PanelDisplayRelink.Plan(
            new[] { Rec("p1", "old", Fp(1111)) },
            new[] { Disp("new", Fp(0)) });

        var move = Assert.Single(plan.Moves);
        Assert.True(move.Weak);
    }

    [Theory]
    [InlineData(0L)]
    [InlineData(0x01010101L)]
    public void Weak_serial_with_single_candidate_moves(long serial)
    {
        var plan = PanelDisplayRelink.Plan(
            new[] { Rec("p1", "old", Fp(serial)) },
            new[] { Disp("new", Fp(serial)) });

        Assert.Equal("new", Assert.Single(plan.Moves).NewDisplayId);
    }

    [Fact]
    public void Two_identical_monitors_with_weak_serials_do_nothing()
    {
        var plan = PanelDisplayRelink.Plan(
            new[] { Rec("p1", "old", Fp(0)) },
            new[] { Disp("a", Fp(0)), Disp("b", Fp(0)) });

        Assert.Empty(plan.Moves);
    }

    [Fact]
    public void Two_orphans_of_one_model_do_nothing()
    {
        var plan = PanelDisplayRelink.Plan(
            new[] { Rec("p1", "old1", Fp(0)), Rec("p2", "old2", Fp(0)) },
            new[] { Disp("new", Fp(0)) });

        Assert.Empty(plan.Moves);
    }

    [Fact]
    public void Two_orphans_with_identical_strong_fingerprints_do_nothing()
    {
        var plan = PanelDisplayRelink.Plan(
            new[] { Rec("p1", "old1", Fp(7)), Rec("p2", "old2", Fp(7)) },
            new[] { Disp("new", Fp(7)) });

        Assert.Empty(plan.Moves);
    }

    [Fact]
    public void Strong_serials_pair_each_orphan_with_its_own_monitor()
    {
        var plan = PanelDisplayRelink.Plan(
            new[] { Rec("p1", "old1", Fp(1)), Rec("p2", "old2", Fp(2)) },
            new[] { Disp("newB", Fp(2)), Disp("newA", Fp(1)) });

        Assert.Equal(
            new[] { ("p1", "newA"), ("p2", "newB") },
            plan.Moves.OrderBy(m => m.RecordId).Select(m => (m.RecordId, m.NewDisplayId)).ToArray());
    }

    [Fact]
    public void Display_owned_by_a_live_record_is_never_taken()
    {
        var plan = PanelDisplayRelink.Plan(
            new[] { Rec("orphan", "old", Fp(5)), Rec("live", "owned", Fp(5)) },
            new[] { Disp("owned", Fp(5)) });

        Assert.Empty(plan.Moves);
    }

    [Fact]
    public void Live_record_without_fingerprint_still_claims_its_display()
    {
        var plan = PanelDisplayRelink.Plan(
            new[] { Rec("orphan", "old", Fp(0)), Rec("live", "owned", null) },
            new[] { Disp("owned", Fp(0)) });

        Assert.Empty(plan.Moves);
        var backfill = Assert.Single(plan.Backfills);
        Assert.Equal("live", backfill.RecordId);
    }

    [Fact]
    public void Different_model_never_matches()
    {
        var plan = PanelDisplayRelink.Plan(
            new[] { Rec("p1", "old", Fp(9)) },
            new[] { Disp("new", Fp(9, mfg: "AOC", product: "2790")) });

        Assert.Empty(plan.Moves);
    }

    [Fact]
    public void Backfill_stamps_fingerprint_on_attached_record_only()
    {
        var plan = PanelDisplayRelink.Plan(
            new[] { Rec("p1", "here", null), Rec("p2", "gone", null) },
            new[] { Disp("here", Fp(3)) });

        Assert.Equal("p1", Assert.Single(plan.Backfills).RecordId);
        Assert.Empty(plan.Moves);
    }

    [Fact]
    public void Record_without_fingerprint_is_never_an_orphan_to_move()
    {
        var plan = PanelDisplayRelink.Plan(
            new[] { Rec("p1", "DEL41B7-5-abc-0-UID1", null) },
            new[] { Disp("DEL41B7-5-def-0-UID9", Fp(123)) });

        Assert.Empty(plan.Moves);
    }

    [Fact]
    public void Windows_fallback_id_on_the_record_side_relinks_to_the_pnp_id()
    {
        var plan = PanelDisplayRelink.Plan(
            new[] { Rec("p1", @"\\.\DISPLAY3", Fp(44)) },
            new[] { Disp("DEL41B7-5-4-UID44", Fp(44)) });

        Assert.Equal("DEL41B7-5-4-UID44", Assert.Single(plan.Moves).NewDisplayId);
    }

    [Fact]
    public void Windows_fallback_id_on_the_display_side_without_fingerprint_is_not_a_candidate()
    {
        var plan = PanelDisplayRelink.Plan(
            new[] { Rec("p1", "DEL41B7-old", Fp(44)) },
            new[] { Disp(@"\\.\DISPLAY2", null) });

        Assert.Empty(plan.Moves);
    }

    [Fact]
    public void Windows_fallback_id_display_with_fingerprint_can_be_a_target()
    {
        var plan = PanelDisplayRelink.Plan(
            new[] { Rec("p1", "DEL41B7-old", Fp(44)) },
            new[] { Disp(@"\\.\DISPLAY2", Fp(44)) });

        Assert.Equal(@"\\.\DISPLAY2", Assert.Single(plan.Moves).NewDisplayId);
    }

    [Theory]
    [InlineData(1920, 515, 1)]
    [InlineData(515, 1920, 1)]
    [InlineData(1920, 1080, 0)]
    public void Y60_family_record_relinks_only_onto_a_display_at_its_native_resolution(int width, int height, int moves)
    {
        var fp = Fp(0, null, "RTK", "BC32");
        var plan = PanelDisplayRelink.Plan(
            new[] { new RelinkRecord("p1", "old", fp, null, "hyte-y60") },
            new[] { new RelinkDisplay("new", fp, false, width, height) });

        Assert.Equal(moves, plan.Moves.Count);
    }

    [Fact]
    public void Registry_applies_backfill_then_relink_and_keeps_layout()
    {
        var store = new InMemoryConfigStore();
        var registry = new PanelDeviceRegistry(store);
        var (record, _) = registry.AllocateForDisplay("old-id", "Side panel", null);
        store.Update(s =>
        {
            s.PanelDevices[record.Id].Enabled = false;
            s.PanelDevices[record.Id].ThemeMode = "dark";
        });

        // Scan 1: monitor attached under its original id, fingerprint is backfilled.
        Assert.Empty(registry.ReconcileDisplayFingerprints(new[] { Disp("old-id", Fp(77)) }).Applied);
        Assert.Equal(77, store.Load().PanelDevices[record.Id].DisplayFingerprint!.Serial);

        // Scan 2: same hardware under a new id.
        var moves = registry.ReconcileDisplayFingerprints(new[] { Disp("new-id", Fp(77)) }).Applied;

        Assert.Equal(("old-id", "new-id"), (moves.Single().OldDisplayId, moves.Single().NewDisplayId));
        var stored = store.Load().PanelDevices[record.Id];
        Assert.Equal("new-id", stored.DisplayId);
        Assert.Equal("dark", stored.ThemeMode);
        Assert.False(stored.Enabled);
        Assert.Single(store.Load().PanelDevices);
    }

    [Fact]
    public void Registry_relink_clears_the_reported_kiosk_viewport()
    {
        var store = new InMemoryConfigStore();
        var registry = new PanelDeviceRegistry(store);
        var (record, _) = registry.AllocateForDisplay("old-id", "Side panel", null);
        registry.Patch(record.Id, new PanelDevicePatch { KioskViewport = new KioskViewportDto { CssWidth = 1920, CssHeight = 1152, Dpr = 1 } });
        registry.ReconcileDisplayFingerprints(new[] { Disp("old-id", Fp(77)) });
        Assert.NotNull(store.Load().PanelDevices[record.Id].KioskViewport);

        registry.ReconcileDisplayFingerprints(new[] { Disp("new-id", Fp(77)) });

        Assert.Null(store.Load().PanelDevices[record.Id].KioskViewport);
    }

    [Fact]
    public void Registry_never_resurrects_a_deleted_record()
    {
        var store = new InMemoryConfigStore();
        var registry = new PanelDeviceRegistry(store);
        var (record, _) = registry.AllocateForDisplay("old-id", null, null);
        registry.ReconcileDisplayFingerprints(new[] { Disp("old-id", Fp(77)) });
        registry.Remove(record.Id);

        var moves = registry.ReconcileDisplayFingerprints(new[] { Disp("new-id", Fp(77)) }).Applied;

        Assert.Empty(moves);
        Assert.Empty(store.Load().PanelDevices);
    }

    [Fact]
    public void Registry_relink_makes_auto_promotion_skip_the_display()
    {
        var store = new InMemoryConfigStore();
        var clock = 0L;
        var registry = new PanelDeviceRegistry(store) { ClockMs = () => clock };
        var (record, _) = registry.AllocateForDisplay("old-id", "Edge", null);
        registry.ReconcileDisplayFingerprints(new[] { Disp("old-id", Fp(0, mfg: "CRX", product: "ED00")) });

        registry.ReconcileDisplayFingerprints(new[] { Disp("new-id", Fp(0, mfg: "CRX", product: "ED00")) });
        clock = 6000;
        registry.ReconcileDisplayFingerprints(new[] { Disp("new-id", Fp(0, mfg: "CRX", product: "ED00")) });

        Assert.Equal(record.Id, registry.FindByDisplayId("new-id")?.Id);
        Assert.Null(registry.FindByDisplayId("old-id"));
    }

    [Fact]
    public void Edid_blob_yields_manufacturer_product_serial_and_descriptor_text()
    {
        var edid = new byte[128];
        edid[1] = edid[2] = edid[3] = edid[4] = edid[5] = edid[6] = 0xFF;
        // DEL: D=4 E=5 L=12 -> 00010 00101 01100
        edid[8] = 0x10; edid[9] = 0xAC;
        edid[10] = 0xB7; edid[11] = 0x41;
        edid[12] = 0x78; edid[13] = 0x56; edid[14] = 0x34; edid[15] = 0x12;
        edid[72 + 3] = 0xFF;
        var text = System.Text.Encoding.ASCII.GetBytes("SN-42\n");
        text.CopyTo(edid, 72 + 5);

        var fp = EdidFingerprint.FromEdid(edid)!;

        Assert.Equal("DEL", fp.Manufacturer);
        Assert.Equal("41B7", fp.Product);
        Assert.Equal(0x12345678, fp.Serial);
        Assert.Equal("SN-42", fp.SerialText);
    }

    [Fact]
    public void Non_edid_blob_yields_null()
    {
        Assert.Null(EdidFingerprint.FromEdid(new byte[128]));
        Assert.Null(EdidFingerprint.FromEdid(new byte[10]));
    }

    [Fact]
    public void FromParts_requires_manufacturer_and_product()
    {
        Assert.Null(EdidFingerprint.FromParts("", "41B7"));
        Assert.Null(EdidFingerprint.FromParts("DEL", null));
        Assert.Equal(5, EdidFingerprint.FromParts("DEL", "41B7", 5)!.Serial);
    }

    private static (PanelDeviceRegistry Registry, InMemoryConfigStore Store, string RecordId, System.Func<long> Now, System.Action<long> Set) WeakSetup()
    {
        var store = new InMemoryConfigStore();
        var clock = 0L;
        var registry = new PanelDeviceRegistry(store) { ClockMs = () => clock };
        var (record, _) = registry.AllocateForDisplay("old-id", null, null);
        registry.ReconcileDisplayFingerprints(new[] { Disp("old-id", Fp(0)) });
        return (registry, store, record.Id, () => clock, t => clock = t);
    }

    [Fact]
    public void Weak_plan_seen_once_is_a_no_op_and_requests_a_recheck()
    {
        var (registry, store, id, _, _) = WeakSetup();

        var result = registry.ReconcileDisplayFingerprints(new[] { Disp("new-id", Fp(0)) });

        Assert.Empty(result.Applied);
        Assert.True(result.PendingWeak);
        Assert.Equal("old-id", store.Load().PanelDevices[id].DisplayId);
    }

    [Fact]
    public void Weak_plan_seen_on_two_scans_five_seconds_apart_applies()
    {
        var (registry, store, id, _, set) = WeakSetup();
        var scan = new[] { Disp("new-id", Fp(0)) };

        registry.ReconcileDisplayFingerprints(scan);
        set(4999);
        Assert.Empty(registry.ReconcileDisplayFingerprints(scan).Applied);
        set(5000);
        var result = registry.ReconcileDisplayFingerprints(scan);

        Assert.Single(result.Applied);
        Assert.False(result.PendingWeak);
        Assert.Equal("new-id", store.Load().PanelDevices[id].DisplayId);
    }

    [Fact]
    public void Weak_plan_that_changes_between_scans_resets_the_timer()
    {
        var (registry, store, id, _, set) = WeakSetup();

        registry.ReconcileDisplayFingerprints(new[] { Disp("new-a", Fp(0)) });
        set(4000);
        registry.ReconcileDisplayFingerprints(new[] { Disp("new-b", Fp(0)) });
        set(8000);
        Assert.Empty(registry.ReconcileDisplayFingerprints(new[] { Disp("new-b", Fp(0)) }).Applied);
        set(9000);
        Assert.Single(registry.ReconcileDisplayFingerprints(new[] { Disp("new-b", Fp(0)) }).Applied);
        Assert.Equal("new-b", store.Load().PanelDevices[id].DisplayId);
    }

    [Fact]
    public void Weak_plan_that_disappears_between_scans_resets_the_timer()
    {
        var (registry, store, id, _, set) = WeakSetup();

        registry.ReconcileDisplayFingerprints(new[] { Disp("new-id", Fp(0)) });
        set(3000);
        registry.ReconcileDisplayFingerprints(new[] { Disp("old-id", Fp(0)) });
        set(9000);
        Assert.Empty(registry.ReconcileDisplayFingerprints(new[] { Disp("new-id", Fp(0)) }).Applied);
        Assert.Equal("old-id", store.Load().PanelDevices[id].DisplayId);
    }

    [Fact]
    public void Strong_serial_relink_applies_on_the_first_scan()
    {
        var store = new InMemoryConfigStore();
        var registry = new PanelDeviceRegistry(store) { ClockMs = () => 0 };
        var (record, _) = registry.AllocateForDisplay("old-id", null, null);
        registry.ReconcileDisplayFingerprints(new[] { Disp("old-id", Fp(5)) });

        var result = registry.ReconcileDisplayFingerprints(new[] { Disp("new-id", Fp(5)) });

        Assert.Single(result.Applied);
        Assert.False(result.PendingWeak);
        Assert.Equal("new-id", store.Load().PanelDevices[record.Id].DisplayId);
    }

    private static void LookalikeScenario(DisplayFingerprint a, DisplayFingerprint b)
    {
        var store = new InMemoryConfigStore();
        var clock = 0L;
        var registry = new PanelDeviceRegistry(store) { ClockMs = () => clock };
        var (record, _) = registry.AllocateForDisplay("A", null, null);

        registry.ReconcileDisplayFingerprints(new[] { Disp("A", a), Disp("B", b) });
        Assert.Contains("B", store.Load().PanelDevices[record.Id].SiblingDisplayIds!);

        // A is off or unplugged; B is an unclaimed lookalike.
        for (var i = 0; i < 4; i++)
        {
            clock += 6000;
            Assert.Empty(registry.ReconcileDisplayFingerprints(new[] { Disp("B", b) }).Applied);
        }
        Assert.Equal("A", store.Load().PanelDevices[record.Id].DisplayId);
    }

    [Fact]
    public void Lookalike_seen_beside_the_panel_monitor_is_never_a_target()
        => LookalikeScenario(Fp(0), Fp(0));

    [Fact]
    public void Lookalike_with_duplicated_placeholder_serial_text_is_never_a_target()
        => LookalikeScenario(Fp(0, "0000000000000"), Fp(0, "0000000000000"));

    [Fact]
    public void Sibling_exclusion_blocks_the_strong_rule_too()
    {
        var plan = PanelDisplayRelink.Plan(
            new[] { Rec("p1", "A", Fp(5), new[] { "B" }) },
            new[] { Disp("B", Fp(5)) });

        Assert.Empty(plan.Moves);
    }

    [Fact]
    public void Genuine_id_change_still_relinks_and_keeps_the_sibling_list()
    {
        var store = new InMemoryConfigStore();
        var registry = new PanelDeviceRegistry(store) { ClockMs = () => 0 };
        var (record, _) = registry.AllocateForDisplay("A", null, null);
        registry.ReconcileDisplayFingerprints(new[] { Disp("A", Fp(5)), Disp("C", Fp(9, mfg: "AOC", product: "2790")) });

        registry.ReconcileDisplayFingerprints(new[] { Disp("A2", Fp(5)), Disp("C", Fp(9, mfg: "AOC", product: "2790")) });

        var stored = store.Load().PanelDevices[record.Id];
        Assert.Equal("A2", stored.DisplayId);
        Assert.Contains("C", stored.SiblingDisplayIds!);
    }

    [Fact]
    public void Sibling_list_drops_the_oldest_at_the_cap_and_writes_only_on_change()
    {
        var full = Enumerable.Range(0, PanelDisplayRelink.MaxSiblings).Select(i => $"S{i}").ToList();
        var scan = new[] { Disp("A", Fp(1)), Disp("S3", null), Disp("NEW", null) };

        var plan = PanelDisplayRelink.Plan(new[] { Rec("p1", "A", Fp(1), full) }, scan);
        var siblings = Assert.Single(plan.SiblingUpdates).Siblings;

        Assert.Equal(PanelDisplayRelink.MaxSiblings, siblings.Count);
        Assert.DoesNotContain("S0", siblings);
        Assert.Equal("NEW", siblings[^1]);
        Assert.Equal(siblings.Count, siblings.Distinct().Count());
        Assert.Empty(PanelDisplayRelink.Plan(new[] { Rec("p1", "A", Fp(1), siblings) }, scan).SiblingUpdates);
    }

    [Fact]
    public void Weak_stored_fingerprint_is_upgraded_when_the_live_one_is_stronger()
    {
        var plan = PanelDisplayRelink.Plan(
            new[] { Rec("p1", "A", Fp(0)) },
            new[] { Disp("A", Fp(77, "SN1")) });

        var upgrade = Assert.Single(plan.Backfills);
        Assert.Equal(77, upgrade.Fingerprint.Serial);
    }

    [Fact]
    public void Equal_or_weaker_live_fingerprint_does_not_overwrite()
    {
        var plan = PanelDisplayRelink.Plan(
            new[] { Rec("p1", "A", Fp(77, "SN1")) },
            new[] { Disp("A", Fp(0)) });

        Assert.Empty(plan.Backfills);
    }

    [Fact]
    public void Duplicate_strong_fingerprints_in_one_scan_are_treated_as_weak()
    {
        var plan = PanelDisplayRelink.Plan(
            new[] { Rec("p1", "A", null), Rec("p2", "B", null) },
            new[] { Disp("A", Fp(0, "SAME")), Disp("B", Fp(0, "SAME")) });

        Assert.All(plan.Backfills, b => Assert.Equal(0, b.Fingerprint.Serial + (b.Fingerprint.SerialText?.Length ?? 0)));
    }

    [Fact]
    public void Duplicate_strong_candidate_cannot_take_the_strong_path()
    {
        var plan = PanelDisplayRelink.Plan(
            new[] { Rec("p1", "gone", Fp(0, "SAME")) },
            new[] { Disp("X", Fp(0, "SAME")), Disp("Y", Fp(0, "SAME")) });

        Assert.Empty(plan.Moves);
    }

    [Fact]
    public void Client_responses_carry_neither_fingerprint_nor_sibling_list()
    {
        var store = new InMemoryConfigStore();
        var registry = new PanelDeviceRegistry(store) { ClockMs = () => 0 };
        var (record, _) = registry.AllocateForDisplay("A", null, null);
        registry.ReconcileDisplayFingerprints(new[] { Disp("A", Fp(5)), Disp("B", Fp(6)) });
        var stored = store.Load().PanelDevices[record.Id];
        Assert.NotNull(stored.DisplayFingerprint);
        Assert.NotNull(stored.SiblingDisplayIds);

        foreach (var response in new[] { registry.Get(record.Id)!, registry.List().Single(), registry.FindByDisplayId("A")! })
        {
            Assert.Null(response.DisplayFingerprint);
            Assert.Null(response.SiblingDisplayIds);
            var json = System.Text.Json.JsonSerializer.Serialize(response, Nexus.Service.Serialization.AppJsonContext.Default.PanelDeviceRecord);
            Assert.DoesNotContain("isplayFingerprint", json);
            Assert.DoesNotContain("iblingDisplayIds", json);
        }
    }

    [Fact]
    public void Edid_serial_text_is_trimmed_of_nul_padding()
    {
        var edid = new byte[128];
        edid[1] = edid[2] = edid[3] = edid[4] = edid[5] = edid[6] = 0xFF;
        edid[75] = 0xFF;
        var text = new byte[13];
        System.Text.Encoding.ASCII.GetBytes("SN9").CopyTo(text, 0);
        text.CopyTo(edid, 77);

        Assert.Equal("SN9", EdidFingerprint.FromEdid(edid)!.SerialText);
    }

    [Fact]
    public void Sibling_order_is_newest_last()
    {
        var plan = PanelDisplayRelink.Plan(
            new[] { Rec("p1", "A", Fp(1), new[] { "X", "B", "Y" }) },
            new[] { Disp("A", Fp(1)), Disp("N", null), Disp("B", null) });

        Assert.Equal(new[] { "X", "Y", "B", "N" }, Assert.Single(plan.SiblingUpdates).Siblings);
    }

    [Fact]
    public void Attached_twin_survives_sixteen_churned_ids()
    {
        var siblings = new List<string> { "TWIN" };
        for (var i = 0; i < 20; i++)
        {
            var scan = new[] { Disp("A", Fp(1)), Disp("TWIN", null), Disp($"CHURN{i}", null) };
            var update = PanelDisplayRelink.Plan(new[] { Rec("p1", "A", Fp(1), siblings) }, scan).SiblingUpdates;
            if (update.Count > 0) siblings = update[0].Siblings;
        }

        Assert.Contains("TWIN", siblings);
        Assert.Equal(PanelDisplayRelink.MaxSiblings, siblings.Count);
        Assert.Equal("CHURN19", siblings[^1]);
    }

    [Fact]
    public void Attached_ids_are_never_evicted_when_they_exceed_the_cap()
    {
        var many = Enumerable.Range(0, 20).Select(i => Disp($"S{i}", null)).Append(Disp("A", Fp(1))).ToArray();

        var first = Assert.Single(PanelDisplayRelink.Plan(new[] { Rec("p1", "A", Fp(1)) }, many).SiblingUpdates).Siblings;

        Assert.Equal(20, first.Count);
        Assert.Empty(PanelDisplayRelink.Plan(new[] { Rec("p1", "A", Fp(1), first) }, many).SiblingUpdates);
    }

    [Fact]
    public void Weak_move_to_a_primary_display_is_a_no_op()
    {
        var plan = PanelDisplayRelink.Plan(
            new[] { Rec("p1", "old", Fp(0)) },
            new[] { new RelinkDisplay("new", Fp(0), IsPrimary: true) });

        Assert.Empty(plan.Moves);
    }

    [Fact]
    public void Strong_move_to_a_primary_display_still_applies()
    {
        var plan = PanelDisplayRelink.Plan(
            new[] { Rec("p1", "old", Fp(5)) },
            new[] { new RelinkDisplay("new", Fp(5), IsPrimary: true) });

        var move = Assert.Single(plan.Moves);
        Assert.False(move.Weak);
    }
}
