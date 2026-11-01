using System;
using System.Threading;
using System.Threading.Tasks;
using Nexus.Service.Peripherals.IoSafety;
using Xunit;

namespace Nexus.Service.Tests.IoSafety;

public class IoGateTests
{
    [Fact]
    public void Close_with_no_call_in_flight_releases_at_once_after_cancelling()
    {
        var gate = new IoGate();
        var order = new System.Collections.Generic.List<string>();

        gate.Close(() => order.Add("cancel"), () => order.Add("release"));

        Assert.Equal(new[] { "cancel", "release" }, order);
        Assert.True(gate.Released);
    }

    [Fact]
    public void Close_does_not_block_and_the_last_exit_releases()
    {
        var gate = new IoGate();
        Assert.True(gate.TryEnter());
        Assert.True(gate.TryEnter());
        var released = 0;

        gate.Close(() => { }, () => Interlocked.Increment(ref released));

        Assert.Equal(0, released);
        Assert.False(gate.Released);
        gate.Exit();
        Assert.Equal(0, released);
        gate.Exit();
        Assert.Equal(1, released);
        Assert.True(gate.Released);
    }

    [Fact]
    public void No_call_enters_once_teardown_started_and_a_refused_entry_leaves_the_count_clean()
    {
        var gate = new IoGate();
        gate.Close(() => { }, () => { });

        Assert.False(gate.TryEnter());
        Assert.Equal(0, gate.InFlight);
    }

    [Fact]
    public async Task A_call_in_flight_at_close_defers_the_release_until_it_returns()
    {
        var gate = new IoGate();
        var inCall = new Barrier(2);
        var leave = new ManualResetEventSlim();
        var released = new ManualResetEventSlim();
        var call = Task.Run(() =>
        {
            Assert.True(gate.TryEnter());
            inCall.SignalAndWait();
            leave.Wait();
            gate.Exit();
        });
        inCall.SignalAndWait();

        gate.Close(() => { }, released.Set);
        Assert.False(released.IsSet);
        leave.Set();
        await call;

        Assert.True(released.Wait(TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public async Task Callers_racing_teardown_never_run_inside_a_released_handle()
    {
        var gate = new IoGate();
        var freed = false;
        var violations = 0;
        var releases = 0;
        var start = new Barrier(5);
        var callers = new Task[4];
        for (var c = 0; c < callers.Length; c++)
        {
            callers[c] = Task.Run(() =>
            {
                start.SignalAndWait();
                for (var i = 0; i < 100_000; i++)
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
        start.SignalAndWait();

        gate.Close(() => { }, () =>
        {
            Volatile.Write(ref freed, true);
            Interlocked.Increment(ref releases);
        });
        await Task.WhenAll(callers);

        Assert.Equal(0, violations);
        Assert.Equal(1, releases);
        Assert.True(gate.Released);
    }
}
