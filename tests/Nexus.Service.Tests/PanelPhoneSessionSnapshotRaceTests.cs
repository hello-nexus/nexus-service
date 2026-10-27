using System.Threading;
using Nexus.Service.Panel;
using Nexus.Service.Persistence;
using Nexus.Service.Relay;

namespace Nexus.Service.Tests;

public class PanelPhoneSessionSnapshotRaceTests
{
    private static PanelPhoneSessionToken Session(int i, long now) => new()
    {
        Id = "s" + i,
        Hash = "h" + i,
        Name = "Phone " + i,
        CreatedAt = now,
        LastSeenAt = now,
        RelayKey = Convert.ToBase64String(new byte[RelayCrypto.RelayRootLength]),
    };

    [Fact]
    public async Task GetActiveRelaySessions_survives_concurrent_session_list_mutation()
    {
        var dir = Path.Combine(Path.GetTempPath(), "nexus-snapshot-race-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            using var store = new JsonConfigStore(Path.Combine(dir, "settings.json"));
            var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            store.Update(s =>
            {
                s.Auth ??= new AuthSettings();
                for (var i = 0; i < 20; i++)
                    s.Auth.PanelPhoneSessions.Add(Session(i, now));
            });
            var service = new PanelPhonePairingService(store, new Nexus.Service.Sockets.MultiplexHub());

            using var stop = new CancellationTokenSource();
            var writer = Task.Run(() =>
            {
                var n = 100;
                while (!stop.IsCancellationRequested)
                {
                    store.Update(s =>
                    {
                        var list = s.Auth!.PanelPhoneSessions;
                        list.Insert(0, Session(n++, now));
                        list.RemoveAt(list.Count - 1);
                    });
                }
            });

            try
            {
                for (var i = 0; i < 20000; i++)
                    Assert.NotEmpty(service.GetActiveRelaySessions());
            }
            finally
            {
                stop.Cancel();
                await writer;
            }
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch { /* temp cleanup */ }
        }
    }
}
