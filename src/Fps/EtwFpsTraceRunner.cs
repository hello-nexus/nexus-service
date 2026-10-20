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

    /// <summary>Stops leaked loggers under the Nexus-Fps- prefix, except any whose
    /// name starts with <paramref name="keepPrefix"/> (this process's own).</summary>
    void SweepStale(string keepPrefix);
}

internal sealed class EtwFpsTraceRunner : IFpsTraceRunner
{
    public const string SessionPrefix = "Nexus-Fps-";
    private static readonly Guid DxgKrnlProviderGuid = new("802EC45A-1E99-4B83-9920-87C98277BA9D");
    private static readonly TraceEventID PresentInfoEventId = (TraceEventID)0x00b8;
    // Present_Info's keyword per the DxgKrnl manifest (PresentMon's
    // Microsoft_Windows_DxgKrnl.h: Present = 0x8000000).
    private const ulong DxgKrnlPresentKeyword = 0x8000000;

    public void Run(string sessionName, Action<int, long> onPresent, CancellationToken token)
    {
        TraceEventSession? session = null;
        Action<TraceEvent>? onEvent = null;
        try
        {
            session = new TraceEventSession(sessionName);
            session.StopOnDispose = true;
            using var stopRegistration = token.Register(static state => StopSession((TraceEventSession?)state), session);
            onEvent = data =>
            {
                if (data.ProviderGuid != DxgKrnlProviderGuid || data.ID != PresentInfoEventId)
                    return;
                onPresent(data.ProcessID, data.TimeStamp.Ticks);
            };
            session.Source.Dynamic.All += onEvent;
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
            session.Source.Process();
        }
        finally
        {
            if (session is not null)
            {
                // Source re-throws the start failure (access denied when not elevated).
                try
                {
                    if (onEvent is not null)
                        session.Source.Dynamic.All -= onEvent;
                }
                catch { }

                StopSession(session);
                StopByName(sessionName);
            }
        }
    }

    public void Stop(string sessionName) => StopByName(sessionName);

    // An ETW logger is a kernel object that outlives the process that made it,
    // so a hard kill or a crash strands one under the dead pid's name. Windows
    // caps loggers at 64 and a full table fails session creation with a bare
    // COM HRESULT, so the whole prefix is swept, not just one name.
    public void SweepStale(string keepPrefix)
    {
        IEnumerable<string> names;
        try { names = TraceEventSession.GetActiveSessionNames(); }
        catch { return; }

        foreach (var name in names)
        {
            if (!name.StartsWith(SessionPrefix, StringComparison.Ordinal)
                || name.StartsWith(keepPrefix, StringComparison.Ordinal))
            {
                continue;
            }

            try { TraceEventSession.GetActiveSession(name)?.Stop(); }
            catch { }
            StopByName(name);
        }
    }

    private static void StopSession(TraceEventSession? session)
    {
        if (session is null)
            return;

        try { session.Source.StopProcessing(); } catch { }
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
