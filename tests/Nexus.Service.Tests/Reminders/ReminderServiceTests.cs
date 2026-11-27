using System.Text.Json;
using Nexus.Service.Reminders;

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
}
