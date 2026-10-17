using System;
using System.Collections.Generic;
using Nexus.Service.Lighting.Engine.Gpu;
using Xunit;

namespace Nexus.Service.Tests.Lighting.Gpu;

public class AsyncProgramBuildTests
{
    private sealed class FakeOps : IProgramBuildOps
    {
        public readonly List<string> Calls = new();
        public int CompileCompleteAfter;
        public int LinkCompleteAfter;
        public readonly Queue<string?> CompileErrors = new();
        public string? LinkError;
        private int _compilePolls, _linkPolls;

        public PendingShaders StartCompile(string fragSource)
        {
            Calls.Add("start:" + fragSource);
            _compilePolls = 0;
            return new PendingShaders(1, 2);
        }

        public bool CompileComplete(PendingShaders s) => ++_compilePolls > CompileCompleteAfter;

        public string? CompileResult(PendingShaders s)
        {
            Calls.Add("compile-result");
            return CompileErrors.Count > 0 ? CompileErrors.Dequeue() : null;
        }

        public uint StartLink(PendingShaders s)
        {
            Calls.Add("link");
            return 7;
        }

        public bool LinkComplete(uint program) => ++_linkPolls > LinkCompleteAfter;

        public string? LinkResult(uint program, PendingShaders s)
        {
            Calls.Add("link-result");
            return LinkError;
        }

        public void Discard(PendingShaders s, uint program) => Calls.Add("discard:" + program);
    }

    private static AsyncProgramBuild Build(FakeOps ops, string? fallback = null, double[]? clock = null, double forceAfter = 1000)
    {
        var i = 0;
        Func<double> now = () => clock is null ? 0 : clock[Math.Min(i++, clock.Length - 1)];
        return new AsyncProgramBuild(ops, "frag", fallback, now, forceAfter);
    }

    [Fact]
    public void Never_reads_a_result_before_completion_is_reported()
    {
        var ops = new FakeOps { CompileCompleteAfter = 2, LinkCompleteAfter = 1 };
        var b = Build(ops);
        b.Start();

        Assert.Equal(BuildPhase.Compiling, b.Poll());
        Assert.Equal(BuildPhase.Compiling, b.Poll());
        Assert.DoesNotContain("compile-result", ops.Calls);
        Assert.Equal(BuildPhase.Linking, b.Poll());
        Assert.DoesNotContain("link-result", ops.Calls);
        Assert.Equal(BuildPhase.Succeeded, b.Poll());
        Assert.Equal(7u, b.Program);
        Assert.Equal(4, b.Polls);
    }

    [Fact]
    public void Link_is_only_submitted_after_compile_completes()
    {
        var ops = new FakeOps { CompileCompleteAfter = 1 };
        var b = Build(ops);
        b.Start();
        b.Poll();
        Assert.DoesNotContain("link", ops.Calls);
        b.Poll();
        Assert.Contains("link", ops.Calls);
    }

    [Fact]
    public void A_failed_compile_retries_the_fallback_source_and_succeeds()
    {
        var ops = new FakeOps();
        ops.CompileErrors.Enqueue("bad literal");
        var b = Build(ops, fallback: "fixed");

        b.Start();
        var phase = b.Poll();

        Assert.Equal(BuildPhase.Succeeded, phase);
        Assert.True(b.UsedFallback);
        Assert.Equal(new[] { "start:frag", "compile-result", "start:fixed" }, ops.Calls.GetRange(0, 3));
    }

    [Fact]
    public void When_the_fallback_fails_too_the_original_error_stands()
    {
        var ops = new FakeOps();
        ops.CompileErrors.Enqueue("first");
        ops.CompileErrors.Enqueue("second");
        var b = Build(ops, fallback: "fixed");

        b.Start();

        Assert.Equal(BuildPhase.Failed, b.Poll());
        Assert.Equal("first", b.Error);
        Assert.Equal(0u, b.Program);
    }

    [Fact]
    public void A_failed_compile_without_a_fallback_fails_with_its_log()
    {
        var ops = new FakeOps();
        ops.CompileErrors.Enqueue("syntax");
        var b = Build(ops);
        b.Start();

        Assert.Equal(BuildPhase.Failed, b.Poll());
        Assert.Equal("syntax", b.Error);
    }

    [Fact]
    public void A_link_failure_is_reported_without_a_retry()
    {
        var ops = new FakeOps { LinkError = "varying mismatch" };
        var b = Build(ops, fallback: "fixed");
        b.Start();

        Assert.Equal(BuildPhase.Failed, b.Poll());
        Assert.Equal("link failed: varying mismatch", b.Error);
        Assert.False(b.UsedFallback);
    }

    [Fact]
    public void A_driver_that_never_reports_completion_is_read_after_the_bound()
    {
        var ops = new FakeOps { CompileCompleteAfter = int.MaxValue, LinkCompleteAfter = int.MaxValue };
        // start, then one clock read per phase check
        var b = Build(ops, clock: new double[] { 0, 10, 2000, 2000, 4000 }, forceAfter: 1000);
        b.Start();

        Assert.Equal(BuildPhase.Compiling, b.Poll());
        Assert.Equal(BuildPhase.Linking, b.Poll());
        Assert.Equal(BuildPhase.Succeeded, b.Poll());
    }

    [Fact]
    public void Latency_spans_submit_to_completion()
    {
        var ops = new FakeOps();
        var b = Build(ops, clock: new double[] { 100, 100, 130 });
        b.Start();
        b.Poll();

        Assert.Equal(30, b.LatencyMs);
    }

    [Fact]
    public void Abandoning_a_pending_build_frees_what_it_holds_once()
    {
        var ops = new FakeOps { CompileCompleteAfter = 5 };
        var b = Build(ops);
        b.Start();
        b.Poll();

        b.Abandon();
        b.Abandon();

        Assert.Single(ops.Calls, c => c.StartsWith("discard", StringComparison.Ordinal));
        Assert.False(b.IsPending);
    }

    [Fact]
    public void Gate_bounds_in_flight_compiles()
    {
        var gate = new CompileGate(2);
        Assert.True(gate.TryEnter());
        Assert.True(gate.TryEnter());
        Assert.False(gate.TryEnter());
        gate.Exit();
        Assert.True(gate.TryEnter());
        gate.Reset();
        Assert.Equal(0, gate.InFlight);
        gate.Exit();
        Assert.Equal(0, gate.InFlight);
    }
}
