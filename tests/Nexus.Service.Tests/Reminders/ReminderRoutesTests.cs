using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Http.Metadata;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Nexus.Service.Auth;
using Nexus.Service.Reminders;
using Nexus.Service.Tests.Integration;

namespace Nexus.Service.Tests.Reminders;

/// <summary>The reminder routes through the real request pipeline, with an isolated data root.</summary>
public sealed class ReminderRoutesTests : IClassFixture<NexusAppFactory>
{
    private readonly NexusAppFactory _factory;
    private readonly HttpClient _client;

    public ReminderRoutesTests(NexusAppFactory factory)
    {
        _factory = factory;
        _client = factory.CreateClient();
        _client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", factory.Services.GetRequiredService<TokenService>().Token);
        // The factory service is shared by the class; reset to the seeded single list.
        var svc = factory.Services.GetRequiredService<ReminderService>();
        var all = svc.GetAll();
        var keep = all.Lists[0].Id;
        foreach (var l in all.Lists.Skip(1))
        {
            svc.DeleteList(l.Id);
        }
        foreach (var i in svc.GetAll().Items)
        {
            svc.DeleteItem(i.Id);
        }
        svc.PatchList(keep, JsonDocument.Parse("""{"name":"Reminders","color":"#0A84FF","icon":"list","order":0}""").RootElement);
    }

    private static StringContent Json(string body) => new(body, Encoding.UTF8, "application/json");

    private async Task<JsonElement> SendAsync(HttpMethod method, string url, string? body, HttpStatusCode expected)
    {
        var req = new HttpRequestMessage(method, url);
        if (body is not null)
        {
            req.Content = Json(body);
        }
        var res = await _client.SendAsync(req);
        Assert.Equal(expected, res.StatusCode);
        var text = await res.Content.ReadAsStringAsync();
        return text.Length == 0 ? default : JsonDocument.Parse(text).RootElement.Clone();
    }

    private async Task<string> ListIdAsync() => (await SendAsync(HttpMethod.Get, "/api/reminders", null, HttpStatusCode.OK)).GetProperty("lists")[0].GetProperty("id").GetString()!;

    private async Task<JsonElement> AddItemAsync(string listId, string extra = "")
    {
        return await SendAsync(HttpMethod.Post, "/api/reminders/items", $$"""{"listId":"{{listId}}","title":"Buy milk"{{extra}}}""", HttpStatusCode.Created);
    }

    [Fact]
    public async Task Every_reminder_route_is_panel_reachable()
    {
        var routes = _factory.Services.GetServices<EndpointDataSource>().SelectMany(d => d.Endpoints).OfType<RouteEndpoint>()
            .Where(e => e.RoutePattern.RawText?.StartsWith("/api/reminders", StringComparison.Ordinal) == true)
            .ToList();

        Assert.Equal(9, routes.Count);
        Assert.All(routes, e => Assert.NotNull(e.Metadata.GetMetadata<AllowPanelAccess>()));
    }

    [Fact]
    public async Task Fresh_store_is_seeded_with_one_default_list()
    {
        var root = await SendAsync(HttpMethod.Get, "/api/reminders", null, HttpStatusCode.OK);

        var lists = root.GetProperty("lists");
        Assert.Equal(1, lists.GetArrayLength());
        Assert.Equal("Reminders", lists[0].GetProperty("name").GetString());
        Assert.Equal("#0A84FF", lists[0].GetProperty("color").GetString());
        Assert.Equal("list", lists[0].GetProperty("icon").GetString());
        Assert.Equal(0, root.GetProperty("items").GetArrayLength());
    }

    [Fact]
    public async Task List_crud_round_trips()
    {
        var created = await SendAsync(HttpMethod.Post, "/api/reminders/lists", """{"name":" Groceries ","color":"#ff8800","icon":"cart"}""", HttpStatusCode.Created);
        var id = created.GetProperty("id").GetString()!;
        Assert.Equal("Groceries", created.GetProperty("name").GetString());
        Assert.Equal(1, created.GetProperty("order").GetInt32());

        var patched = await SendAsync(HttpMethod.Put, $"/api/reminders/lists/{id}", """{"name":"Food","order":5}""", HttpStatusCode.OK);
        Assert.Equal("Food", patched.GetProperty("name").GetString());
        Assert.Equal("#ff8800", patched.GetProperty("color").GetString());
        Assert.Equal(5, patched.GetProperty("order").GetInt32());

        await SendAsync(HttpMethod.Delete, $"/api/reminders/lists/{id}", null, HttpStatusCode.NoContent);
        var after = await SendAsync(HttpMethod.Get, "/api/reminders", null, HttpStatusCode.OK);
        Assert.Equal(1, after.GetProperty("lists").GetArrayLength());
    }

    [Fact]
    public async Task Deleting_a_list_deletes_its_items_and_the_last_list_is_refused()
    {
        var first = await ListIdAsync();
        var other = (await SendAsync(HttpMethod.Post, "/api/reminders/lists", """{"name":"B","color":"#000000","icon":"list"}""", HttpStatusCode.Created)).GetProperty("id").GetString()!;
        await AddItemAsync(other);
        await AddItemAsync(first);

        await SendAsync(HttpMethod.Delete, $"/api/reminders/lists/{other}", null, HttpStatusCode.NoContent);
        var all = await SendAsync(HttpMethod.Get, "/api/reminders", null, HttpStatusCode.OK);
        Assert.Equal(1, all.GetProperty("items").GetArrayLength());

        var refused = await SendAsync(HttpMethod.Delete, $"/api/reminders/lists/{first}", null, HttpStatusCode.Conflict);
        Assert.Equal("lastList", refused.GetProperty("code").GetString());
    }

    [Theory]
    [InlineData("""{"name":"","color":"#000000","icon":"list"}""", "invalid")]
    [InlineData("""{"name":"x","color":"red","icon":"list"}""", "invalid")]
    [InlineData("""{"name":"x","color":"#000000","icon":"has space"}""", "invalid")]
    [InlineData("""{"name":"x"}""", "invalid")]
    [InlineData("not json", "invalid")]
    public async Task List_create_with_a_bad_body_is_400_with_a_code(string body, string code)
    {
        var err = await SendAsync(HttpMethod.Post, "/api/reminders/lists", body, HttpStatusCode.BadRequest);

        Assert.Equal(code, err.GetProperty("code").GetString());
        Assert.Equal(code, err.GetProperty("error").GetString());
    }

    [Fact]
    public async Task Item_create_returns_201_with_defaults()
    {
        var item = await AddItemAsync(await ListIdAsync());

        Assert.Equal("Buy milk", item.GetProperty("title").GetString());
        Assert.Equal(JsonValueKind.Null, item.GetProperty("notes").ValueKind);
        Assert.Equal(JsonValueKind.Null, item.GetProperty("dueDate").ValueKind);
        Assert.Equal(JsonValueKind.Null, item.GetProperty("completedAt").ValueKind);
        Assert.Equal(0, item.GetProperty("priority").GetInt32());
        Assert.False(item.GetProperty("flagged").GetBoolean());
        Assert.False(item.GetProperty("completed").GetBoolean());
        Assert.Equal(0, item.GetProperty("order").GetInt32());
        Assert.True(item.GetProperty("createdAt").GetInt64() > 0);
    }

    [Fact]
    public async Task Item_create_keeps_every_supplied_field()
    {
        var item = await AddItemAsync(await ListIdAsync(),
            ""","notes":"2% only","dueDate":"2026-12-01","dueTime":"09:30","priority":3,"flagged":true,"url":"https://example.com/a"  """);

        Assert.Equal("2% only", item.GetProperty("notes").GetString());
        Assert.Equal("2026-12-01", item.GetProperty("dueDate").GetString());
        Assert.Equal("09:30", item.GetProperty("dueTime").GetString());
        Assert.Equal(3, item.GetProperty("priority").GetInt32());
        Assert.True(item.GetProperty("flagged").GetBoolean());
        Assert.Equal("https://example.com/a", item.GetProperty("url").GetString());
    }

    [Theory]
    [InlineData(""","dueDate":"2026-13-40" """)]
    [InlineData(""","dueDate":"12/01/2026" """)]
    [InlineData(""","dueDate":"2026-12-01","dueTime":"25:00" """)]
    [InlineData(""","dueTime":"09:30" """)]
    [InlineData(""","priority":4 """)]
    [InlineData(""","priority":-1 """)]
    [InlineData(""","priority":"high" """)]
    [InlineData(""","url":"ftp://example.com/a" """)]
    [InlineData(""","url":"javascript:alert(1)" """)]
    [InlineData(""","url":"not a url" """)]
    [InlineData(""","flagged":"yes" """)]
    public async Task Item_create_with_an_invalid_field_is_400(string extra)
    {
        var listId = await ListIdAsync();

        var err = await SendAsync(HttpMethod.Post, "/api/reminders/items", $$"""{"listId":"{{listId}}","title":"x"{{extra}}}""", HttpStatusCode.BadRequest);

        Assert.Equal("invalid", err.GetProperty("code").GetString());
    }

    [Fact]
    public async Task Item_create_rejects_a_blank_title_and_an_unknown_list()
    {
        var listId = await ListIdAsync();

        var blank = await SendAsync(HttpMethod.Post, "/api/reminders/items", $$"""{"listId":"{{listId}}","title":"  "}""", HttpStatusCode.BadRequest);
        var unknown = await SendAsync(HttpMethod.Post, "/api/reminders/items", """{"listId":"nope","title":"x"}""", HttpStatusCode.NotFound);

        Assert.Equal("invalid", blank.GetProperty("code").GetString());
        Assert.Equal("notFound", unknown.GetProperty("code").GetString());
    }

    [Fact]
    public async Task Length_caps_report_limitReached()
    {
        var listId = await ListIdAsync();
        var longTitle = new string('t', ReminderService.MaxTitle + 1);
        var longNotes = new string('n', ReminderService.MaxNotes + 1);
        var longName = new string('l', ReminderService.MaxName + 1);

        var t = await SendAsync(HttpMethod.Post, "/api/reminders/items", $$"""{"listId":"{{listId}}","title":"{{longTitle}}"}""", HttpStatusCode.BadRequest);
        var n = await SendAsync(HttpMethod.Post, "/api/reminders/items", $$"""{"listId":"{{listId}}","title":"x","notes":"{{longNotes}}"}""", HttpStatusCode.BadRequest);
        var l = await SendAsync(HttpMethod.Post, "/api/reminders/lists", $$"""{"name":"{{longName}}","color":"#000000","icon":"list"}""", HttpStatusCode.BadRequest);

        Assert.Equal("limitReached", t.GetProperty("code").GetString());
        Assert.Equal("limitReached", n.GetProperty("code").GetString());
        Assert.Equal("limitReached", l.GetProperty("code").GetString());
    }

    [Theory]
    [InlineData("""{"order":-1}""")]
    [InlineData("""{"order":1000001}""")]
    [InlineData("""{"order":2147483647}""")]
    public async Task Order_outside_the_allowed_range_is_400(string patch)
    {
        var listId = await ListIdAsync();
        var id = (await AddItemAsync(listId)).GetProperty("id").GetString()!;

        var item = await SendAsync(HttpMethod.Put, $"/api/reminders/items/{id}", patch, HttpStatusCode.BadRequest);
        var list = await SendAsync(HttpMethod.Put, $"/api/reminders/lists/{listId}", patch, HttpStatusCode.BadRequest);

        Assert.Equal("invalid", item.GetProperty("code").GetString());
        Assert.Equal("invalid", list.GetProperty("code").GetString());
    }

    [Theory]
    [InlineData("""{"color":"red"}""")]
    [InlineData("""{"color":null}""")]
    [InlineData("""{"icon":"has space"}""")]
    [InlineData("""{"icon":null}""")]
    [InlineData("""{"name":null}""")]
    [InlineData("""{"name":"   "}""")]
    public async Task List_patch_with_a_bad_field_is_400_and_changes_nothing(string patch)
    {
        var listId = await ListIdAsync();

        var err = await SendAsync(HttpMethod.Put, $"/api/reminders/lists/{listId}", patch, HttpStatusCode.BadRequest);

        Assert.Equal("invalid", err.GetProperty("code").GetString());
        var all = await SendAsync(HttpMethod.Get, "/api/reminders", null, HttpStatusCode.OK);
        Assert.Equal("Reminders", all.GetProperty("lists")[0].GetProperty("name").GetString());
        Assert.Equal("#0A84FF", all.GetProperty("lists")[0].GetProperty("color").GetString());
    }

    [Fact]
    public async Task A_failed_save_is_500_saveFailed_with_no_change_and_no_ping()
    {
        var svc = _factory.Services.GetRequiredService<ReminderService>();
        var listId = await ListIdAsync();
        var dir = Path.Combine(_factory.ConfigDir, "reminders");
        var backup = dir + ".moved";
        var pings = 0;
        void Ping() => Interlocked.Increment(ref pings);
        svc.Changed += Ping;
        Directory.Move(dir, backup);
        File.WriteAllText(dir, "blocker");
        try
        {
            var err = await SendAsync(HttpMethod.Post, "/api/reminders/items", $$"""{"listId":"{{listId}}","title":"x"}""", HttpStatusCode.InternalServerError);
            Assert.Equal("saveFailed", err.GetProperty("code").GetString());
        }
        finally
        {
            File.Delete(dir);
            Directory.Move(backup, dir);
            svc.Changed -= Ping;
        }

        Assert.Equal(0, pings);
        Assert.Empty(svc.GetAll().Items);
    }

    [Fact]
    public async Task Completing_sets_and_clears_completedAt()
    {
        var id = (await AddItemAsync(await ListIdAsync())).GetProperty("id").GetString()!;

        var done = await SendAsync(HttpMethod.Put, $"/api/reminders/items/{id}", """{"completed":true}""", HttpStatusCode.OK);
        Assert.True(done.GetProperty("completed").GetBoolean());
        Assert.True(done.GetProperty("completedAt").GetInt64() > 0);

        var undone = await SendAsync(HttpMethod.Put, $"/api/reminders/items/{id}", """{"completed":false}""", HttpStatusCode.OK);
        Assert.False(undone.GetProperty("completed").GetBoolean());
        Assert.Equal(JsonValueKind.Null, undone.GetProperty("completedAt").ValueKind);
    }

    [Fact]
    public async Task Explicit_null_clears_nullable_fields_and_absent_keeps_them()
    {
        var id = (await AddItemAsync(await ListIdAsync(), ""","notes":"n","dueDate":"2026-12-01","dueTime":"09:30","url":"https://example.com" """)).GetProperty("id").GetString()!;

        var kept = await SendAsync(HttpMethod.Put, $"/api/reminders/items/{id}", """{"priority":2}""", HttpStatusCode.OK);
        Assert.Equal("n", kept.GetProperty("notes").GetString());
        Assert.Equal("09:30", kept.GetProperty("dueTime").GetString());

        var cleared = await SendAsync(HttpMethod.Put, $"/api/reminders/items/{id}", """{"notes":null,"url":null,"dueTime":null}""", HttpStatusCode.OK);
        Assert.Equal(JsonValueKind.Null, cleared.GetProperty("notes").ValueKind);
        Assert.Equal(JsonValueKind.Null, cleared.GetProperty("url").ValueKind);
        Assert.Equal(JsonValueKind.Null, cleared.GetProperty("dueTime").ValueKind);
        Assert.Equal("2026-12-01", cleared.GetProperty("dueDate").GetString());

        var noDate = await SendAsync(HttpMethod.Put, $"/api/reminders/items/{id}", """{"dueTime":"10:00"}""", HttpStatusCode.OK);
        Assert.Equal("10:00", noDate.GetProperty("dueTime").GetString());
        var dateGone = await SendAsync(HttpMethod.Put, $"/api/reminders/items/{id}", """{"dueDate":null}""", HttpStatusCode.OK);
        Assert.Equal(JsonValueKind.Null, dateGone.GetProperty("dueDate").ValueKind);
        Assert.Equal(JsonValueKind.Null, dateGone.GetProperty("dueTime").ValueKind);
    }

    [Fact]
    public async Task Patch_cannot_null_a_required_field_or_set_a_time_without_a_date()
    {
        var id = (await AddItemAsync(await ListIdAsync())).GetProperty("id").GetString()!;

        await SendAsync(HttpMethod.Put, $"/api/reminders/items/{id}", """{"title":null}""", HttpStatusCode.BadRequest);
        await SendAsync(HttpMethod.Put, $"/api/reminders/items/{id}", """{"dueTime":"09:00"}""", HttpStatusCode.BadRequest);
        await SendAsync(HttpMethod.Put, $"/api/reminders/items/{id}", """{"priority":9}""", HttpStatusCode.BadRequest);
        var missing = await SendAsync(HttpMethod.Put, "/api/reminders/items/nope", """{"title":"x"}""", HttpStatusCode.NotFound);
        Assert.Equal("notFound", missing.GetProperty("code").GetString());
    }

    [Fact]
    public async Task Moving_an_item_changes_its_list_and_lands_last()
    {
        var first = await ListIdAsync();
        var other = (await SendAsync(HttpMethod.Post, "/api/reminders/lists", """{"name":"B","color":"#000000","icon":"list"}""", HttpStatusCode.Created)).GetProperty("id").GetString()!;
        await AddItemAsync(other);
        var id = (await AddItemAsync(first)).GetProperty("id").GetString()!;

        var moved = await SendAsync(HttpMethod.Put, $"/api/reminders/items/{id}", $$"""{"listId":"{{other}}"}""", HttpStatusCode.OK);
        var bad = await SendAsync(HttpMethod.Put, $"/api/reminders/items/{id}", """{"listId":"nope"}""", HttpStatusCode.NotFound);

        Assert.Equal(other, moved.GetProperty("listId").GetString());
        Assert.Equal(1, moved.GetProperty("order").GetInt32());
        Assert.Equal("notFound", bad.GetProperty("code").GetString());
    }

    [Fact]
    public async Task Delete_item_is_204_then_404()
    {
        var id = (await AddItemAsync(await ListIdAsync())).GetProperty("id").GetString()!;

        await SendAsync(HttpMethod.Delete, $"/api/reminders/items/{id}", null, HttpStatusCode.NoContent);
        await SendAsync(HttpMethod.Delete, $"/api/reminders/items/{id}", null, HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Clear_completed_removes_only_completed_items_in_scope()
    {
        var first = await ListIdAsync();
        var other = (await SendAsync(HttpMethod.Post, "/api/reminders/lists", """{"name":"B","color":"#000000","icon":"list"}""", HttpStatusCode.Created)).GetProperty("id").GetString()!;
        var a = (await AddItemAsync(first)).GetProperty("id").GetString()!;
        await AddItemAsync(first);
        var c = (await AddItemAsync(other)).GetProperty("id").GetString()!;
        await SendAsync(HttpMethod.Put, $"/api/reminders/items/{a}", """{"completed":true}""", HttpStatusCode.OK);
        await SendAsync(HttpMethod.Put, $"/api/reminders/items/{c}", """{"completed":true}""", HttpStatusCode.OK);

        var scoped = await SendAsync(HttpMethod.Post, "/api/reminders/items/clear-completed", $$"""{"listId":"{{first}}"}""", HttpStatusCode.OK);
        Assert.Equal(1, scoped.GetProperty("removed").GetInt32());
        var rest = await SendAsync(HttpMethod.Post, "/api/reminders/items/clear-completed", "{}", HttpStatusCode.OK);
        Assert.Equal(1, rest.GetProperty("removed").GetInt32());
        var none = await SendAsync(HttpMethod.Post, "/api/reminders/items/clear-completed", "{}", HttpStatusCode.OK);
        Assert.Equal(0, none.GetProperty("removed").GetInt32());
        await SendAsync(HttpMethod.Post, "/api/reminders/items/clear-completed", """{"listId":"nope"}""", HttpStatusCode.NotFound);

        var all = await SendAsync(HttpMethod.Get, "/api/reminders", null, HttpStatusCode.OK);
        Assert.Equal(1, all.GetProperty("items").GetArrayLength());
    }

    [Fact]
    public async Task Reorder_sets_order_by_position_and_get_returns_that_order()
    {
        var listId = await ListIdAsync();
        var ids = new List<string>();
        for (var n = 0; n < 3; n++)
        {
            ids.Add((await AddItemAsync(listId)).GetProperty("id").GetString()!);
        }
        var reversed = Enumerable.Reverse(ids).ToList();

        await SendAsync(HttpMethod.Post, "/api/reminders/reorder", $$"""{"listId":"{{listId}}","itemIds":["{{reversed[0]}}","{{reversed[1]}}","{{reversed[2]}}"]}""", HttpStatusCode.NoContent);

        var all = await SendAsync(HttpMethod.Get, "/api/reminders", null, HttpStatusCode.OK);
        var order = all.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("id").GetString()!).ToList();
        Assert.Equal(reversed, order);
    }

    [Fact]
    public async Task Reorder_must_name_exactly_the_lists_items_and_changes_nothing_otherwise()
    {
        var first = await ListIdAsync();
        var other = (await SendAsync(HttpMethod.Post, "/api/reminders/lists", """{"name":"B","color":"#000000","icon":"list"}""", HttpStatusCode.Created)).GetProperty("id").GetString()!;
        var a = (await AddItemAsync(first)).GetProperty("id").GetString()!;
        var b = (await AddItemAsync(first)).GetProperty("id").GetString()!;
        var foreign = (await AddItemAsync(other)).GetProperty("id").GetString()!;

        var unknownList = await SendAsync(HttpMethod.Post, "/api/reminders/reorder", $$"""{"listId":"nope","itemIds":["{{a}}"]}""", HttpStatusCode.NotFound);
        var cases = new[]
        {
            $$"""["{{b}}","nope"]""",
            $$"""["{{b}}","{{foreign}}"]""",
            $$"""["{{b}}","{{b}}"]""",
            $$"""["{{b}}"]""",
            "[]",
        };
        foreach (var ids in cases)
        {
            var err = await SendAsync(HttpMethod.Post, "/api/reminders/reorder", $$"""{"listId":"{{first}}","itemIds":{{ids}}}""", HttpStatusCode.BadRequest);
            Assert.Equal("stale", err.GetProperty("code").GetString());
        }
        await SendAsync(HttpMethod.Post, "/api/reminders/reorder", """{"listId":"x"}""", HttpStatusCode.BadRequest);

        Assert.Equal("notFound", unknownList.GetProperty("code").GetString());
        var all = await SendAsync(HttpMethod.Get, "/api/reminders", null, HttpStatusCode.OK);
        Assert.Equal(a, all.GetProperty("items")[0].GetProperty("id").GetString());
    }

    [Fact]
    public async Task Reorder_of_an_empty_list_is_a_silent_no_op()
    {
        var svc = _factory.Services.GetRequiredService<ReminderService>();
        var listId = await ListIdAsync();
        var pings = 0;
        void Ping() => Interlocked.Increment(ref pings);
        svc.Changed += Ping;
        try
        {
            await SendAsync(HttpMethod.Post, "/api/reminders/reorder", $$"""{"listId":"{{listId}}","itemIds":[]}""", HttpStatusCode.NoContent);
        }
        finally
        {
            svc.Changed -= Ping;
        }

        Assert.Equal(0, pings);
    }

    [Fact]
    public async Task Clear_completed_accepts_an_empty_body()
    {
        var res = await _client.PostAsync("/api/reminders/items/clear-completed", new StringContent("", Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        Assert.Contains("\"removed\":0", await res.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Mutations_raise_Changed()
    {
        var svc = _factory.Services.GetRequiredService<ReminderService>();
        var pings = 0;
        void Ping() => Interlocked.Increment(ref pings);
        svc.Changed += Ping;
        try
        {
            var id = (await AddItemAsync(await ListIdAsync())).GetProperty("id").GetString()!;
            await SendAsync(HttpMethod.Put, $"/api/reminders/items/{id}", """{"flagged":true}""", HttpStatusCode.OK);
            await SendAsync(HttpMethod.Delete, $"/api/reminders/items/{id}", null, HttpStatusCode.NoContent);
            await SendAsync(HttpMethod.Get, "/api/reminders", null, HttpStatusCode.OK);
        }
        finally
        {
            svc.Changed -= Ping;
        }

        Assert.Equal(3, pings);
    }
}
