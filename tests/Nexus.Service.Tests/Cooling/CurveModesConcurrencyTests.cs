using System;
using System.Threading;
using System.Threading.Tasks;
using Nexus.Service.Cooling;
using Nexus.Service.Persistence;
using Xunit;

namespace Nexus.Service.Tests.Cooling;

public class CurveModesConcurrencyTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "nexus-curvemodes-" + Guid.NewGuid().ToString("N"));
    private readonly JsonConfigStore _store;

    public CurveModesConcurrencyTests()
    {
        Directory.CreateDirectory(_dir);
        _store = new JsonConfigStore(Path.Combine(_dir, "settings.json"));
    }

    public void Dispose()
    {
        _store.Dispose();
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    private static CurveDocument Curve(string id, string output) => new()
    {
        Id = id,
        Outputs = { new CurveOutputDocument { Id = output, Type = "Fan" } },
    };

    [Fact]
    public void BoundFanIds_lists_every_curve_output()
    {
        _store.Update(s =>
        {
            s.Cooling.Curves.Add(Curve("a", "fan-a"));
            s.Cooling.Curves.Add(Curve("b", "fan-b"));
        });

        Assert.Equal(new[] { "fan-a", "fan-b" }, CurveModes.BoundFanIds(_store).OrderBy(x => x));
    }

    [Fact]
    public async Task BoundFanIds_survives_curve_edits_on_another_thread()
    {
        using var stop = new CancellationTokenSource();
        var editor = Task.Run(() =>
        {
            var n = 0;
            while (!stop.IsCancellationRequested)
            {
                var id = $"c{n++ % 8}";
                _store.Update(s =>
                {
                    s.Cooling.Curves.Add(Curve(id, "fan-" + id));
                    if (s.Cooling.Curves.Count > 6)
                    {
                        s.Cooling.Curves.RemoveAt(0);
                    }
                });
            }
        });

        try
        {
            for (var i = 0; i < 20_000; i++)
            {
                CurveModes.BoundFanIds(_store);
            }
        }
        finally
        {
            stop.Cancel();
            await editor;
        }
    }
}
