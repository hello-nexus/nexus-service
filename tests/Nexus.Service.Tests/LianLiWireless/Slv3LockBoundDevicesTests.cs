using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Nexus.Service.Auth;
using Nexus.Service.Peripherals.LianLiWireless;
using Nexus.Service.Persistence;
using Nexus.Service.Tests.Integration;

namespace Nexus.Service.Tests.LianLiWireless;

public sealed class Slv3LockBoundDevicesTests : IDisposable
{
    private readonly NexusAppFactory _factory = new();
    private readonly HttpClient _client;

    public Slv3LockBoundDevicesTests()
    {
        _client = _factory.CreateClient();
        _client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer", _factory.Services.GetRequiredService<TokenService>().Token);
    }

    public void Dispose()
    {
        _client.Dispose();
        _factory.Dispose();
    }

    [Fact]
    public async Task The_lock_defaults_on_and_the_put_route_persists_it_and_the_state_reports_it()
    {
        using (var before = JsonDocument.Parse(await _client.GetStringAsync("/devices/lianli-wireless/state")))
        {
            Assert.True(before.RootElement.GetProperty("lockBoundDevices").GetBoolean());
        }

        var off = await _client.PutAsJsonAsync("/devices/lianli-wireless/lock", new { enabled = false });
        Assert.True(off.IsSuccessStatusCode);
        using (var after = JsonDocument.Parse(await _client.GetStringAsync("/devices/lianli-wireless/state")))
        {
            Assert.False(after.RootElement.GetProperty("lockBoundDevices").GetBoolean());
        }
        var store = _factory.Services.GetRequiredService<IConfigStore>();
        Assert.False(store.Load().Devices.LianLiWireless.LockBoundDevices);
        Assert.False(_factory.Services.GetRequiredService<Slv3Hub>().State.LockBoundDevices);

        await _client.PutAsJsonAsync("/devices/lianli-wireless/lock", new { enabled = true });
        Assert.True(store.Load().Devices.LianLiWireless.LockBoundDevices);
    }

    [Fact]
    public void A_settings_file_without_the_value_reads_on_and_a_saved_off_survives_a_restart()
    {
        var dir = Path.Combine(Path.GetTempPath(), "nexus-test-lock-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, "settings.json");
        try
        {
            File.WriteAllText(path, """{ "devices": { "lianLiWireless": { "ownedDevices": { "112233445566": 2 } } } }""");
            using (var legacy = new JsonConfigStore(path))
            {
                var settings = legacy.Load().Devices.LianLiWireless;
                Assert.True(settings.LockBoundDevices);
                Assert.Equal(2, settings.OwnedDevices["112233445566"]);
                legacy.Update(s => s.Devices.LianLiWireless.LockBoundDevices = false);
                legacy.FlushNow();
            }

            using var restarted = new JsonConfigStore(path);
            var reloaded = restarted.Load().Devices.LianLiWireless;
            Assert.False(reloaded.LockBoundDevices);
            Assert.Equal(2, reloaded.OwnedDevices["112233445566"]);
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { }
        }
    }
}
