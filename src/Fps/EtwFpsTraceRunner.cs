using System.Runtime.InteropServices;
using Microsoft.Diagnostics.Tracing;
using Microsoft.Diagnostics.Tracing.Session;

namespace Nexus.Service.Fps;

/// <summary>One ETW present-capture session, behind a seam so the provider's
/// lifecycle and recovery can be tested without a kernel logger.</summary>
internal interface IFpsTraceRunner
{
    /// <summary>Blocks, delivering (pid, timestamp ticks) per present, until the
    /// token is cancelled (return or throw) or the logger ends on its own
    /// (return). Throws when the session cannot start.</summary>
    void Run(string sessionName, Action<int, long> onPresent, CancellationToken token);

    /// <summary>Stops the named logger. Idempotent; never throws.</summary>
    void Stop(string sessionName);

    /// <summary>Stops leaked loggers under the Nexus-Fps- prefix, except this
    /// process's own at or above the current capture's sequence number.</summary>
    void SweepStale(string currentSessionName);
}

internal sealed class EtwFpsTraceRunner : IFpsTraceRunner
{
    public const string SessionPrefix = "Nexus-Fps-";
    private static readonly Guid DxgKrnlProviderGuid = new("802EC45A-1E99-4B83-9920-87C98277BA9D");
    private static readonly TraceEventID PresentInfoEventId = (TraceEventID)0x00b8;
    // Present_Info's keyword per the DxgKrnl manifest (PresentMon's
    // Microsoft_Windows_DxgKrnl.h).
    private const ulong DxgKrnlPresentKeyword = 0x8000000;

    public void Run(string sessionName, Action<int, long> onPresent, CancellationToken token)
    {
        TraceEventSession? session = null;
        ETWTraceEventSource? source = null;
        Action<TraceEvent>? onEvent = null;
        try
        {
            session = new TraceEventSession(sessionName);
            session.StopOnDispose = true;
            // Read once: after Dispose the Source getter and EnableProvider
            // would start a fresh logger (EnsureStarted never checks the stop
            // state), and Process would then block on it forever.
            source = session.Source;
            var liveSession = session;
            var liveSource = source;
            using var stopRegistration = token.Register(() => StopSession(liveSession, liveSource));
            if (token.IsCancellationRequested)
                return;

            onEvent = data =>
            {
                if (data.ProviderGuid != DxgKrnlProviderGuid || data.ID != PresentInfoEventId)
                    return;
                onPresent(data.ProcessID, data.TimeStamp.Ticks);
            };
            source.Dynamic.All += onEvent;
            if (token.IsCancellationRequested)
                return;

            // A bare EnableProvider means every keyword at Verbose - the full
            // DxgKrnl stream (scheduler, VidMm, HAGS logging), all marshalled
            // into managed code while the handler keeps only Present_Info.
            // Keyword + event-id filtering bounds the session to presents.
            session.EnableProvider(
                DxgKrnlProviderGuid,
                TraceEventLevel.Informational,
                matchAnyKeywords: DxgKrnlPresentKeyword,
                options: new TraceEventProviderOptions
                {
                    EventIDsToEnable = new[] { (int)PresentInfoEventId },
                });
            if (token.IsCancellationRequested)
                return;

            source.Process();
        }
        finally
        {
            if (session is not null)
            {
                // Source re-throws the start failure (access denied when not
                // elevated), which would fault this task after FailCapture
                // dropped it.
                try
                {
                    if (onEvent is not null && source is not null)
                        source.Dynamic.All -= onEvent;
                }
                catch { }

                StopSession(session, source);
                StopByName(sessionName);
            }
        }
    }

    public void Stop(string sessionName) => StopByName(sessionName);

    // An ETW logger is a kernel object that outlives the process that made it,
    // so a hard kill or a crash strands one under the dead pid's name. Windows
    // caps loggers at 64 and a full table fails session creation with a bare
    // COM HRESULT, so the whole prefix is swept, not just one name.
    public void SweepStale(string currentSessionName)
    {
        IEnumerable<string> names;
        try { names = TraceEventSession.GetActiveSessionNames(); }
        catch { return; }

        foreach (var name in names)
        {
            if (!ShouldSweep(name, currentSessionName))
                continue;

            try { TraceEventSession.GetActiveSession(name)?.Stop(); }
            catch { }
            StopByName(name);
        }
    }

    /// <summary>True for a Nexus-Fps- logger that is not this process's current or
    /// newer capture: other pids, legacy names without a sequence, and this
    /// pid's older sequence numbers (their owner task is gone or ending).</summary>
    internal static bool ShouldSweep(string name, string currentSessionName)
    {
        if (!name.StartsWith(SessionPrefix, StringComparison.Ordinal))
            return false;

        if (!TryParse(name, out var pid, out var seq) || !TryParse(currentSessionName, out var curPid, out var curSeq))
            return true;

        return pid != curPid || seq < curSeq;
    }

    private static bool TryParse(string name, out int pid, out int seq)
    {
        pid = 0;
        seq = 0;
        var rest = name.StartsWith(SessionPrefix, StringComparison.Ordinal) ? name[SessionPrefix.Length..] : "";
        var dash = rest.IndexOf('-');
        return dash > 0
            && int.TryParse(rest[..dash], out pid)
            && int.TryParse(rest[(dash + 1)..], out seq);
    }

    private static void StopSession(TraceEventSession? session, ETWTraceEventSource? source)
    {
        if (session is null)
            return;

        try { source?.StopProcessing(); } catch { }
        try { session.Stop(); } catch { }
        try { session.Dispose(); } catch { }
    }

    private static void StopByName(string sessionName)
    {
        var properties = new EventTraceProperties();
        properties.Wnode.BufferSize = (uint)Marshal.SizeOf<EventTraceProperties>();
        try { _ = ControlTrace(0, sessionName, ref properties, EventTraceControlStop); }
        catch { }
    }

    private const uint EventTraceControlStop = 1;

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint ControlTrace(
        ulong sessionHandle,
        string sessionName,
        ref EventTraceProperties properties,
        uint controlCode);

    [StructLayout(LayoutKind.Sequential)]
    private struct EventTraceProperties
    {
        public WnodeHeader Wnode;
        public uint BufferSize;
        public uint MinimumBuffers;
        public uint MaximumBuffers;
        public uint MaximumFileSize;
        public uint LogFileMode;
        public uint FlushTimer;
        public uint EnableFlags;
        public int AgeLimit;
        public int NumberOfBuffers;
        public uint FreeBuffers;
        public uint EventsLost;
        public uint BuffersWritten;
        public uint LogBuffersLost;
        public uint RealTimeBuffersLost;
        public IntPtr LoggerThreadId;
        public uint LogFileNameOffset;
        public uint LoggerNameOffset;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WnodeHeader
    {
        public uint BufferSize;
        public uint ProviderId;
        public ulong HistoricalContext;
        public ulong TimeStamp;
        public Guid Guid;
        public uint ClientContext;
        public uint Flags;
    }
}
