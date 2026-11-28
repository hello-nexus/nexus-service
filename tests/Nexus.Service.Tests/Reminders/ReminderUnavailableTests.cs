using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Nexus.Service.Auth;
using Nexus.Service.Reminders;
using Nexus.Service.Tests.Integration;

namespace Nexus.Service.Tests.Reminders;

/// <summary>A store that cannot be loaded answers 503 unavailable on the read and on a mutation.</summary>
public sealed class ReminderUnavailableTests : IClassFixture<NexusAppFactory>
{
    private readonly NexusAppFactory _factory;

    public ReminderUnavailableTests(NexusAppFactory factory) => _factory = factory;

    [Fact]
    public async Task Get_and_mutations_are_503_unavailable_while_the_store_cannot_be_read()
    {
        var root = Path.Combine(Path.GetTempPath(), "nexus-reminders-503-" + Guid.NewGuid().ToString("N"));
        try
        {
            using var blocked = _factory.WithWebHostBuilder(b => b.ConfigureServices(services =>
            {
                services.RemoveAll<ReminderService>();
                services.AddSingleton(new ReminderService(root, null, _ => throw new UnauthorizedAccessException(), TimeSpan.Zero));
            }));
            var client = blocked.CreateClient();
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", blocked.Services.GetRequiredService<TokenService>().Token);

            var get = await client.GetAsync("/api/reminders");
            var post = await client.PostAsync("/api/reminders/items", new StringContent("""{"listId":"x","title":"t"}""", Encoding.UTF8, "application/json"));

            Assert.Equal(HttpStatusCode.ServiceUnavailable, get.StatusCode);
            Assert.Equal(HttpStatusCode.ServiceUnavailable, post.StatusCode);
            Assert.Equal("unavailable", JsonDocument.Parse(await get.Content.ReadAsStringAsync()).RootElement.GetProperty("code").GetString());
            Assert.Equal("unavailable", JsonDocument.Parse(await post.Content.ReadAsStringAsync()).RootElement.GetProperty("code").GetString());
        }
        finally
        {
            try
            {
                Directory.Delete(root, recursive: true);
            }
            catch (IOException)
            {
            }
        }
    }
}
