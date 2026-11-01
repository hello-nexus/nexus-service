using System;
using System.Threading;
using System.Threading.Tasks;
using Nexus.Service.Persistence;
using Nexus.Service.Routes;
using Nexus.Service.Serialization;
using Xunit;

namespace Nexus.Service.Tests.Lighting;

public class LightingSettingsSnapshotTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "nexus-snapshot-" + Guid.NewGuid().ToString("N"));
    private readonly JsonConfigStore _store;

    public LightingSettingsSnapshotTests()
    {
        Directory.CreateDirectory(_dir);
        _store = new JsonConfigStore(Path.Combine(_dir, "settings.json"));
    }

    public void Dispose()
    {
        _store.Dispose();
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    [Fact]
    public void Snapshot_is_a_copy_that_later_edits_do_not_reach()
    {
        _store.Update(s => s.Lighting.Animate.States["rainbow"] = new AnimateEffectState { Speed = 7 });

        var snapshot = LightingRoutes.SnapshotUnderLock(_store, s => s.Lighting.Animate, AppJsonContext.Default.AnimateSettings);
        _store.Update(s => s.Lighting.Animate.States["rainbow"].Speed = 99);

        Assert.Equal(7, snapshot.States["rainbow"].Speed);
    }

    [Fact]
    public async Task Snapshot_walks_the_state_dictionaries_while_another_thread_edits_them()
    {
        using var stop = new CancellationTokenSource();
        var editor = Task.Run(() =>
        {
            var n = 0;
            while (!stop.IsCancellationRequested)
            {
                var key = $"fx{n++ % 16}";
                _store.Update(s =>
                {
                    s.Lighting.Animate.States[key] = new AnimateEffectState { Params = { ["p"] = n } };
                    s.Lighting.Animate.States.Remove($"fx{(n + 8) % 16}");
                    s.Lighting.Static.States[key] = new AnimateEffectState();
                    s.Lighting.Static.States.Remove($"fx{(n + 8) % 16}");
                });
            }
        });

        try
        {
            for (var i = 0; i < 5_000; i++)
            {
                LightingRoutes.SnapshotUnderLock(_store, s => s.Lighting.Animate, AppJsonContext.Default.AnimateSettings);
                LightingRoutes.SnapshotUnderLock(_store, s => s.Lighting.Static, AppJsonContext.Default.StaticSettings);
            }
        }
        finally
        {
            stop.Cancel();
            await editor;
        }
    }
}
