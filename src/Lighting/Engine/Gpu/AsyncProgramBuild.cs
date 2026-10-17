using System;

namespace Nexus.Service.Lighting.Engine.Gpu;

/// <summary>
/// Counts background compiles in flight so a burst of thumbnail, validation and
/// warm-up requests reaches the driver's worker threads a few programs at a time.
/// GL thread only. Live compiles never take a slot: nothing queues in front of them.
/// </summary>
internal sealed class CompileGate
{
    private readonly int _max;
    private int _inFlight;

    public CompileGate(int max) => _max = max;

    public int InFlight => _inFlight;

    public bool TryEnter()
    {
        if (_inFlight >= _max)
        {
            return false;
        }
        _inFlight++;
        return true;
    }

    public void Exit()
    {
        if (_inFlight > 0)
        {
            _inFlight--;
        }
    }

    public void Reset() => _inFlight = 0;
}

/// <summary>One program's in-flight build and whether it holds a <see cref="CompileGate"/> slot.</summary>
internal sealed class BuildHolder
{
    public AsyncProgramBuild? Build;
    public bool Held;
}

/// <summary>What a step run through <see cref="GpuContext.InvokeStep"/> needs next.</summary>
public enum StepState { Done, Poll, WaitSlot }

internal enum BuildPhase { Idle, Compiling, Linking, Succeeded, Failed }

/// <summary>Shader objects whose compile has been submitted and not yet read back.</summary>
internal readonly record struct PendingShaders(uint Vertex, uint Fragment);

/// <summary>
/// The GL calls <see cref="AsyncProgramBuild"/> drives. The Complete members
/// must be non-forcing (a COMPLETION_STATUS query); the Result members may
/// block, and are only called once the matching Complete member reported true.
/// </summary>
internal interface IProgramBuildOps
{
    PendingShaders StartCompile(string fragSource);
    bool CompileComplete(PendingShaders shaders);
    /// <summary>Null when both shaders compiled; otherwise the info log, with the shaders freed.</summary>
    string? CompileResult(PendingShaders shaders);
    uint StartLink(PendingShaders shaders);
    bool LinkComplete(uint program);
    /// <summary>Null when the program linked (shaders detached and freed); otherwise the info log, with everything freed.</summary>
    string? LinkResult(uint program, PendingShaders shaders);
    /// <summary>Free whatever is still held, for a build dropped before it finished.</summary>
    void Discard(PendingShaders shaders, uint program);
}

/// <summary>
/// Compile and link one program without ever waiting on the driver: each
/// <see cref="Poll"/> advances as far as the non-forcing completion queries
/// allow and returns. The fragment-literal retry the synchronous path has runs
/// here as a second compile when the first one fails. A phase still incomplete
/// after <c>forceAfterMs</c> is treated as complete, so a driver that never
/// reports completion costs one blocking read instead of an effect that never
/// renders. GL thread only.
/// </summary>
internal sealed class AsyncProgramBuild
{
    /// <summary>Upper bound on a pending phase before the blocking read is taken.</summary>
    public const double DefaultForceAfterMs = 15_000;

    private readonly IProgramBuildOps _ops;
    private readonly string _fragSource;
    private readonly string? _fallbackSource;
    private readonly Func<double> _nowMs;
    private readonly double _forceAfterMs;
    private PendingShaders _shaders;
    private uint _program;
    private double _startedMs;
    private double _phaseStartedMs;
    private string? _firstError;

    public AsyncProgramBuild(IProgramBuildOps ops, string fragSource, string? fallbackSource,
        Func<double> nowMs, double forceAfterMs = DefaultForceAfterMs)
    {
        _ops = ops;
        _fragSource = fragSource;
        _fallbackSource = fallbackSource;
        _nowMs = nowMs;
        _forceAfterMs = forceAfterMs;
    }

    public BuildPhase Phase { get; private set; }
    public bool IsPending => Phase is BuildPhase.Compiling or BuildPhase.Linking;
    public uint Program => Phase == BuildPhase.Succeeded ? _program : 0;
    public string? Error { get; private set; }
    /// <summary>The fallback source produced the shader.</summary>
    public bool UsedFallback { get; private set; }
    public int Polls { get; private set; }
    /// <summary>Submit to completion, set when the build finishes either way.</summary>
    public double LatencyMs { get; private set; }

    public void Start()
    {
        if (Phase != BuildPhase.Idle)
        {
            return;
        }
        _startedMs = _phaseStartedMs = _nowMs();
        _shaders = _ops.StartCompile(_fragSource);
        Phase = BuildPhase.Compiling;
    }

    public BuildPhase Poll()
    {
        Polls++;
        while (IsPending)
        {
            var now = _nowMs();
            var forced = now - _phaseStartedMs >= _forceAfterMs;
            if (Phase == BuildPhase.Compiling)
            {
                if (!forced && !_ops.CompileComplete(_shaders))
                {
                    break;
                }
                var err = _ops.CompileResult(_shaders);
                if (err is null)
                {
                    _program = _ops.StartLink(_shaders);
                    Phase = BuildPhase.Linking;
                    _phaseStartedMs = now;
                    continue;
                }
                if (_fallbackSource is not null && _firstError is null)
                {
                    _firstError = err;
                    UsedFallback = true;
                    _shaders = _ops.StartCompile(_fallbackSource);
                    _phaseStartedMs = now;
                    continue;
                }
                // When the retry fails too, the original error stands.
                Finish(BuildPhase.Failed, _firstError ?? err, now);
            }
            else
            {
                if (!forced && !_ops.LinkComplete(_program))
                {
                    break;
                }
                var err = _ops.LinkResult(_program, _shaders);
                Finish(err is null ? BuildPhase.Succeeded : BuildPhase.Failed, err is null ? null : "link failed: " + err, now);
            }
        }
        return Phase;
    }

    /// <summary>Drop a build that will not be finished (disposed effect). Frees what it holds.</summary>
    public void Abandon()
    {
        if (!IsPending)
        {
            return;
        }
        _ops.Discard(_shaders, Phase == BuildPhase.Linking ? _program : 0);
        Phase = BuildPhase.Failed;
        Error = "abandoned";
    }

    private void Finish(BuildPhase phase, string? error, double now)
    {
        Phase = phase;
        Error = error;
        if (phase == BuildPhase.Failed)
        {
            _program = 0;
        }
        LatencyMs = now - _startedMs;
    }
}
