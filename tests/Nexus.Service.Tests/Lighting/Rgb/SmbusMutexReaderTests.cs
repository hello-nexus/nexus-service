using Nexus.Service.Lighting.Rgb;

namespace Nexus.Service.Tests.Lighting.Rgb;

public class SmbusMutexReaderTests
{
    private sealed class FakeHandle : ISmbusMutexHandle
    {
        internal bool Acquires = true;
        internal bool Abandoned;
        internal Exception? Throws;
        internal int Releases;
        internal int Disposals;
        internal TimeSpan? WaitedFor;

        public bool WaitOne(TimeSpan timeout)
        {
            WaitedFor = timeout;
            if (Throws is not null)
            {
                throw Throws;
            }
            if (Abandoned)
            {
                throw new AbandonedMutexException();
            }
            return Acquires;
        }

        public void Release() => Releases++;

        public void Dispose() => Disposals++;
    }

    [Fact]
    public void An_acquired_mutex_is_released_at_once_and_reads_free()
    {
        var handle = new FakeHandle();

        var state = SmbusMutexReader.Wait(() => handle, TimeSpan.FromSeconds(4));

        Assert.Equal("free", state);
        Assert.Equal(1, handle.Releases);
        Assert.Equal(1, handle.Disposals);
        Assert.Equal(TimeSpan.FromSeconds(4), handle.WaitedFor);
    }

    [Fact]
    public void A_timeout_reads_held_and_releases_nothing()
    {
        var handle = new FakeHandle { Acquires = false };

        var state = SmbusMutexReader.Wait(() => handle, TimeSpan.FromSeconds(4));

        Assert.Equal("held", state);
        Assert.Equal(0, handle.Releases);
        Assert.Equal(1, handle.Disposals);
    }

    [Fact]
    public void An_abandoned_mutex_is_acquired_then_released_and_reads_abandoned()
    {
        var handle = new FakeHandle { Abandoned = true };

        var state = SmbusMutexReader.Wait(() => handle, TimeSpan.FromSeconds(4));

        Assert.Equal("abandoned", state);
        Assert.Equal(1, handle.Releases);
    }

    [Fact]
    public void A_mutex_that_does_not_exist_reads_absent()
    {
        Assert.Equal("absent", SmbusMutexReader.Wait(() => null, TimeSpan.FromSeconds(4)));
    }

    [Fact]
    public void An_open_or_wait_failure_reads_as_the_exception_type_and_never_throws()
    {
        Assert.Equal(nameof(UnauthorizedAccessException), SmbusMutexReader.Wait(() => throw new UnauthorizedAccessException(), TimeSpan.Zero));

        var handle = new FakeHandle { Throws = new InvalidOperationException() };
        Assert.Equal(nameof(InvalidOperationException), SmbusMutexReader.Wait(() => handle, TimeSpan.Zero));
        Assert.Equal(0, handle.Releases);
    }

    [Fact]
    public void Off_windows_the_real_mutex_reads_not_applicable()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        Assert.Equal("n/a", SmbusMutexReader.Wait(TimeSpan.Zero));
    }
}
