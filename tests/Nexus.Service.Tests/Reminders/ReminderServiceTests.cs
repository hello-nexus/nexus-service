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
        var pings = 0;
        svc.Changed += () => pings++;
        Directory.Delete(Path.Combine(_root, "reminders"), recursive: true);
        File.WriteAllText(Path.Combine(_root, "reminders"), "blocker");

        var created = svc.CreateItem(J($$"""{"listId":"{{listId}}","title":"x"}"""));
        var list = svc.CreateList(J("""{"name":"B","color":"#000000","icon":"list"}"""));
        var patch = svc.PatchList(listId, J("""{"name":"Renamed"}"""));
        var delete = svc.DeleteList(listId);

        Assert.Equal("saveFailed", created.Error);
        Assert.Equal("saveFailed", list.Error);
        Assert.Equal("saveFailed", patch.Error);
        Assert.NotNull(delete.Error);
        Assert.Equal(0, pings);
        var all = svc.GetAll();
        Assert.Empty(all.Items);
        Assert.Equal("Reminders", Assert.Single(all.Lists).Name);
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
        });

        Assert.Equal("keep", Assert.Single(svc.GetAll().Items).Title);
        Assert.Empty(Directory.GetFiles(Path.Combine(_root, "reminders"), "*.corrupt-*"));
    }

    [Fact]
    public void A_persistent_read_failure_refuses_mutations_and_never_overwrites_the_file()
    {
        var first = new ReminderService(_root);
        var listId = first.GetAll().Lists[0].Id;
        first.CreateItem(J($$"""{"listId":"{{listId}}","title":"keep"}"""));
        var before = File.ReadAllText(StorePath);

        var svc = new ReminderService(_root, null, _ => throw new IOException("locked"));
        var result = svc.CreateItem(J($$"""{"listId":"{{svc.GetAll().Lists[0].Id}}","title":"new"}"""));

        Assert.Equal("saveFailed", result.Error);
        Assert.Equal(before, File.ReadAllText(StorePath));
        Assert.Empty(Directory.GetFiles(Path.Combine(_root, "reminders"), "*.corrupt-*"));
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
