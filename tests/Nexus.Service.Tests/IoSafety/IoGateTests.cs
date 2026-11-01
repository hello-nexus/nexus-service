using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using Nexus.Service.Peripherals.IoSafety;
using Xunit;

namespace Nexus.Service.Tests.IoSafety;

public class IoGateTests
{
    [Fact]
    public async Task Close_waits_for_a_call_in_flight_before_reporting_the_handle_free()
    {
        var gate = new IoGate();
        Assert.True(gate.TryEnter());
        var cancelled = 0;
        var released = new ManualResetEventSlim();
        var call = Task.Run(() =>
        {
            // The blocked call returns only once teardown cancelled it, as a pending overlapped read does.
            Assert.True(released.Wait(TimeSpan.FromSeconds(10)));
            Thread.Sleep(100);
            gate.Exit();
        });

        var drained = gate.CloseAndDrain(() => { Interlocked.Increment(ref cancelled); released.Set(); }, timeoutMs: 5000);

        Assert.True(drained);
        Assert.Equal(1, cancelled);
        Assert.Equal(0, gate.InFlight);
        await call;
    }

    [Fact]
    public void Close_reports_not_drained_when_a_call_never_leaves()
    {
        var gate = new IoGate();
        Assert.True(gate.TryEnter());
        var sw = Stopwatch.StartNew();

        Assert.False(gate.CloseAndDrain(() => { }, timeoutMs: 200));

        Assert.InRange(sw.ElapsedMilliseconds, 150, 5000);
        Assert.Equal(1, gate.InFlight);
    }

    [Fact]
    public void No_call_enters_once_teardown_started()
    {
        var gate = new IoGate();
        Assert.True(gate.CloseAndDrain(() => { }, 100));

        Assert.False(gate.TryEnter());
        Assert.Equal(0, gate.InFlight);
    }

    [Fact]
    public async Task Callers_racing_teardown_never_run_inside_a_freed_handle()
    {
        var gate = new IoGate();
        var freed = false;
        var violations = 0;
        var callers = new Task[4];
        for (var c = 0; c < callers.Length; c++)
        {
            callers[c] = Task.Run(() =>
            {
                for (var i = 0; i < 200_000; i++)
                {
                    if (!gate.TryEnter())
                    {
                        return;
                    }
                    try
                    {
                        if (Volatile.Read(ref freed))
                        {
                            Interlocked.Increment(ref violations);
                        }
                    }
                    finally
                    {
                        gate.Exit();
                    }
                }
            });
        }
        await Task.Delay(5);

        Assert.True(gate.CloseAndDrain(() => { }, 5000));
        Volatile.Write(ref freed, true);
        await Task.WhenAll(callers);

        Assert.Equal(0, violations);
    }
}
