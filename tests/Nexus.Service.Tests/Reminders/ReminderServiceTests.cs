using System.Text.Json;
using Nexus.Service.Models.Reminders;
using Nexus.Service.Persistence;
using Nexus.Service.Reminders;
using Nexus.Service.Serialization;

namespace Nexus.Service.Tests.Reminders;

public sealed class ReminderServiceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "nexus-reminders-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private static JsonElement J(string json) => JsonDocument.Parse(json).RootElement.Clone();

    [Fact]
    public void Writes_survive_a_restart_with_no_flush_step()
    {
        var first = new ReminderService(_root);
        var listId = first.GetAll().Lists[0].Id;
        var item = first.CreateItem(J($$"""{"listId":"{{listId}}","title":"Persist me","priority":2}""")).Value!;
        first.PatchItem(item.Id, J("""{"completed":true}"""));

        var second = new ReminderService(_root);
        var all = second.GetAll();

        Assert.Single(all.Lists);
        Assert.Equal(listId, all.Lists[0].Id);
        var reloaded = Assert.Single(all.Items);
        Assert.Equal("Persist me", reloaded.Title);
        Assert.True(reloaded.Completed);
        Assert.NotNull(reloaded.CompletedAt);
        Assert.True(File.Exists(Path.Combine(_root, "reminders", "reminders.json")));
    }

    [Fact]
    public void A_corrupt_file_is_kept_aside_and_a_fresh_list_is_seeded()
    {
        Directory.CreateDirectory(Path.Combine(_root, "reminders"));
        File.WriteAllText(Path.Combine(_root, "reminders", "reminders.json"), "{ not json");

        var svc = new ReminderService(_root);

        Assert.Equal("Reminders", Assert.Single(svc.GetAll().Lists).Name);
        Assert.Single(Directory.GetFiles(Path.Combine(_root, "reminders"), "reminders.json.corrupt-*"));
    }

    [Fact]
    public async Task Concurrent_creates_all_land_with_distinct_orders()
    {
        var svc = new ReminderService(_root);
        var listId = svc.GetAll().Lists[0].Id;
        var body = J($$"""{"listId":"{{listId}}","title":"x"}""");

        await Task.WhenAll(Enumerable.Range(0, 50).Select(_ => Task.Run(() => svc.CreateItem(body))));

        var items = svc.GetAll().Items;
        Assert.Equal(50, items.Count);
        Assert.Equal(50, items.Select(i => i.Order).Distinct().Count());
    }

    private string StorePath => Path.Combine(_root, "reminders", "reminders.json");

    [Fact]
    public void A_failed_save_rolls_back_returns_saveFailed_and_raises_nothing()
    {
        var svc = new ReminderService(_root);
        var listId = svc.GetAll().Lists[0].Id;
        var second = svc.CreateList(J("""{"name":"Second","color":"#000000","icon":"list"}""")).Value!;
        var pings = 0;
        svc.Changed += () => pings++;
        Directory.Delete(Path.Combine(_root, "reminders"), recursive: true);
        File.WriteAllText(Path.Combine(_root, "reminders"), "blocker");

        var created = svc.CreateItem(J($$"""{"listId":"{{listId}}","title":"x"}"""));
        var list = svc.CreateList(J("""{"name":"B","color":"#000000","icon":"list"}"""));
        var patch = svc.PatchList(listId, J("""{"name":"Renamed"}"""));
        var delete = svc.DeleteList(second.Id);

        Assert.Equal("saveFailed", created.Error);
        Assert.Equal("saveFailed", list.Error);
        Assert.Equal("saveFailed", patch.Error);
        Assert.Equal("saveFailed", delete.Error);
        Assert.Equal(0, pings);
        var all = svc.GetAll();
        Assert.Empty(all.Items);
        Assert.Equal(2, all.Lists.Count);
        Assert.Equal("Reminders", all.Lists[0].Name);
    }

    [Fact]
    public void A_transient_read_lock_is_retried_and_not_treated_as_corruption()
    {
        new ReminderService(_root).CreateItem(J($$"""{"listId":"{{new ReminderService(_root).GetAll().Lists[0].Id}}","title":"keep"}"""));
        var failures = 2;

        var svc = new ReminderService(_root, null, path =>
        {
            if (failures-- > 0)
            {
                throw new IOException("sharing violation");
            }
            return File.ReadAllText(path);
        }, TimeSpan.Zero);

        Assert.Equal("keep", Assert.Single(svc.GetAll().Items).Title);
        Assert.Empty(Directory.GetFiles(Path.Combine(_root, "reminders"), "*.corrupt-*"));
    }

    private sealed class ManualTime : TimeProvider
    {
        private DateTimeOffset _now = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan by) => _now += by;
    }

    [Fact]
    public void A_persistent_read_failure_is_unavailable_never_overwrites_and_recovers_lazily()
    {
        var first = new ReminderService(_root);
        var listId = first.GetAll().Lists[0].Id;
        first.CreateItem(J($$"""{"listId":"{{listId}}","title":"keep"}"""));
        var before = File.ReadAllText(StorePath);
        var locked = true;
        var time = new ManualTime();

        var svc = new ReminderService(_root, time, path => locked ? throw new IOException("locked") : File.ReadAllText(path), TimeSpan.Zero);
        var read = svc.Read();
        var write = svc.CreateItem(J($$"""{"listId":"{{listId}}","title":"new"}"""));
        locked = false;
        var tooSoon = svc.Read();
        time.Advance(ReminderService.LoadRetryInterval + TimeSpan.FromSeconds(1));
        var recovered = svc.Read();

        Assert.Equal("unavailable", read.Error);
        Assert.Equal("unavailable", write.Error);
        Assert.Equal("unavailable", tooSoon.Error);
        Assert.Equal("keep", Assert.Single(recovered.Value!.Items).Title);
        Assert.Equal(before, File.ReadAllText(StorePath));
        Assert.Empty(Directory.GetFiles(Path.Combine(_root, "reminders"), "*.corrupt-*"));
    }

    [Fact]
    public void An_unreadable_backup_blocks_a_missing_or_corrupt_main()
    {
        var first = new ReminderService(_root);
        var listId = first.GetAll().Lists[0].Id;
        first.CreateItem(J($$"""{"listId":"{{listId}}","title":"keep"}"""));
        var bak = AtomicJsonFile.BackupPathFor(StorePath);
        Assert.True(File.Exists(bak));
        var bakBefore = File.ReadAllText(bak);
        File.WriteAllText(StorePath, "{ truncated");

        var svc = new ReminderService(_root, null, path => path.EndsWith(".bak", StringComparison.Ordinal) ? throw new IOException("locked") : File.ReadAllText(path), TimeSpan.Zero);

        Assert.Equal("unavailable", svc.Read().Error);
        Assert.Equal("unavailable", svc.CreateItem(J($$"""{"listId":"{{listId}}","title":"x"}""")).Error);
        Assert.Equal("{ truncated", File.ReadAllText(StorePath));
        Assert.Equal(bakBefore, File.ReadAllText(bak));
        Assert.Empty(Directory.GetFiles(Path.Combine(_root, "reminders"), "*.corrupt-*"));
    }

    [Fact]
    public void A_missing_main_recovers_from_the_backup_instead_of_seeding_over_it()
    {
        var first = new ReminderService(_root);
        var listId = first.GetAll().Lists[0].Id;
        first.CreateItem(J($$"""{"listId":"{{listId}}","title":"one"}"""));
        first.CreateItem(J($$"""{"listId":"{{listId}}","title":"two"}"""));
        // A crash between File.Replace's renames leaves the backup and no main file.
        File.Delete(StorePath);
        Assert.True(File.Exists(AtomicJsonFile.BackupPathFor(StorePath)));

        var svc = new ReminderService(_root);

        var all = svc.GetAll();
        Assert.Equal(listId, Assert.Single(all.Lists).Id);
        Assert.NotEmpty(all.Items);
        Assert.True(File.Exists(StorePath));
    }

    [Fact]
    public void A_missing_main_prefers_the_staged_tmp_file()
    {
        var first = new ReminderService(_root);
        var listId = first.GetAll().Lists[0].Id;
        first.CreateItem(J($$"""{"listId":"{{listId}}","title":"one"}"""));
        File.Copy(StorePath, AtomicJsonFile.TempPathFor(StorePath));
        first.CreateItem(J($$"""{"listId":"{{listId}}","title":"two"}"""));
        File.Copy(StorePath, AtomicJsonFile.TempPathFor(StorePath), overwrite: true);
        File.Delete(StorePath);

        var svc = new ReminderService(_root);

        Assert.Equal(2, svc.GetAll().Items.Count);
    }

    [Theory]
    [InlineData("""{"lists":null,"items":[]}""")]
    [InlineData("""{"lists":[null],"items":[]}""")]
    [InlineData("""{"lists":[],"items":null}""")]
    [InlineData("""{"lists":[],"items":[null]}""")]
    public void Null_collections_or_entries_are_corrupt_not_a_crash(string json)
    {
        Directory.CreateDirectory(Path.Combine(_root, "reminders"));
        File.WriteAllText(StorePath, json);

        var svc = new ReminderService(_root);

        Assert.Equal("Reminders", Assert.Single(svc.GetAll().Lists).Name);
        Assert.Single(Directory.GetFiles(Path.Combine(_root, "reminders"), "reminders.json.corrupt-*"));
    }

    [Fact]
    public void Null_entries_in_the_backup_are_skipped_in_favor_of_seeding()
    {
        Directory.CreateDirectory(Path.Combine(_root, "reminders"));
        File.WriteAllText(StorePath, "{ truncated");
        File.WriteAllText(AtomicJsonFile.BackupPathFor(StorePath), """{"lists":[null],"items":[]}""");

        var svc = new ReminderService(_root);

        Assert.Equal("Reminders", Assert.Single(svc.GetAll().Lists).Name);
    }

    [Fact]
    public void Appending_past_the_order_cap_renumbers_so_the_new_item_stays_patchable()
    {
        var svc = new ReminderService(_root);
        var listId = svc.GetAll().Lists[0].Id;
        var first = svc.CreateItem(J($$"""{"listId":"{{listId}}","title":"first"}""")).Value!;
        svc.PatchItem(first.Id, J($$"""{"order":{{ReminderService.MaxOrder}}}"""));

        var created = svc.CreateItem(J($$"""{"listId":"{{listId}}","title":"second"}""")).Value!;
        var patched = svc.PatchItem(created.Id, J("""{"flagged":true}"""));

        Assert.True(created.Order <= ReminderService.MaxOrder);
        Assert.Null(patched.Error);
        var all = svc.GetAll().Items;
        Assert.Equal(new[] { "first", "second" }, all.Select(i => i.Title));
        Assert.Equal(new[] { 0, 1 }, all.Select(i => i.Order));
    }

    [Fact]
    public void Moving_into_a_list_at_the_order_cap_renumbers_the_target_list()
    {
        var svc = new ReminderService(_root);
        var a = svc.GetAll().Lists[0].Id;
        var b = svc.CreateList(J("""{"name":"B","color":"#000000","icon":"list"}""")).Value!.Id;
        var top = svc.CreateItem(J($$"""{"listId":"{{b}}","title":"top"}""")).Value!;
        svc.PatchItem(top.Id, J($$"""{"order":{{ReminderService.MaxOrder}}}"""));
        var mover = svc.CreateItem(J($$"""{"listId":"{{a}}","title":"mover"}""")).Value!;

        var moved = svc.PatchItem(mover.Id, J($$"""{"listId":"{{b}}"}"""));
        var again = svc.PatchItem(mover.Id, J("""{"priority":1}"""));

        Assert.Null(moved.Error);
        Assert.Null(again.Error);
        Assert.True(moved.Value!.Order <= ReminderService.MaxOrder);
    }

    [Fact]
    public void A_null_literal_file_is_corrupt_not_an_empty_store()
    {
        Directory.CreateDirectory(Path.Combine(_root, "reminders"));
        File.WriteAllText(StorePath, "null");

        var svc = new ReminderService(_root);

        Assert.Equal("Reminders", Assert.Single(svc.GetAll().Lists).Name);
        Assert.Single(Directory.GetFiles(Path.Combine(_root, "reminders"), "reminders.json.corrupt-*"));
    }

    [Fact]
    public void A_truncated_main_file_recovers_from_a_good_backup()
    {
        var first = new ReminderService(_root);
        var listId = first.GetAll().Lists[0].Id;
        first.CreateItem(J($$"""{"listId":"{{listId}}","title":"one"}"""));
        first.CreateItem(J($$"""{"listId":"{{listId}}","title":"two"}"""));
        Assert.True(File.Exists(AtomicJsonFile.BackupPathFor(StorePath)));
        var good = File.ReadAllText(StorePath);
        File.WriteAllText(StorePath, good[..(good.Length / 2)]);

        var svc = new ReminderService(_root);

        var all = svc.GetAll();
        Assert.Equal(listId, Assert.Single(all.Lists).Id);
        Assert.NotEmpty(all.Items);
        Assert.Single(Directory.GetFiles(Path.Combine(_root, "reminders"), "reminders.json.corrupt-*"));
        // Recovered state was written back, so the next start reads a valid main file.
        Assert.Equal(all.Items.Count, new ReminderService(_root).GetAll().Items.Count);
    }

    [Fact]
    public void Item_and_list_caps_report_limitReached_from_a_preseeded_store()
    {
        var lists = Enumerable.Range(0, ReminderService.MaxLists).Select(n => new ReminderListDto { Id = "l" + n, Name = "L", Order = n }).ToList();
        var items = Enumerable.Range(0, ReminderService.MaxItems).Select(n => new ReminderDto { Id = "i" + n, ListId = "l0", Title = "t", Order = n }).ToList();
        Directory.CreateDirectory(Path.Combine(_root, "reminders"));
        File.WriteAllText(StorePath, System.Text.Json.JsonSerializer.Serialize(new ReminderStoreFile { Lists = lists, Items = items }, PersistenceJsonContext.Default.ReminderStoreFile));

        var svc = new ReminderService(_root);

        Assert.Equal("limitReached", svc.CreateItem(J("""{"listId":"l0","title":"x"}""")).Error);
        Assert.Equal("limitReached", svc.CreateList(J("""{"name":"x","color":"#000000","icon":"list"}""")).Error);
    }
}
