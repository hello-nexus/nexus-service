using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Nexus.Service.Platform;

namespace Nexus.Service.Peripherals.LianLiFlex;

/// <summary>Immutable snapshot of one unit, replaced whole on every poll.</summary>
public sealed class FlexUnit
{
    public required string Mac { get; init; }
    public required FlexFamily Family { get; init; }
    public required FlexReceiverInfo Info { get; init; }
    public int FirmwareVersion { get; init; }
    public string ScreenSerial { get; init; } = "";
    public bool Stale { get; init; }
    public bool LcdRestarting { get; init; }

    /// <summary>Wired control applies only while the receiver is not bound to an RF master.</summary>
    public bool Controllable => !Info.RfBound;

    public int Fans => Math.Max(1, Info.Fans);

    public int LedCount => Fans * FlexProtocol.LedsPerFan(Family);
}

/// <summary>One receiver connection with its own I/O lock; every exchange with the receiver goes through it.</summary>
internal sealed class FlexUnitLink : IDisposable
{
    public readonly FlexUnitPort Port;
    public readonly IFlexReceiverTransport Transport;
    public readonly object Io = new();
    public volatile FlexReceiverInfo? Info;
    public int FirmwareVersion;
    public int Missed;
    public long LastPollMs;
    public long LastClockMs;
    public long LastLiveMs;
    // -2 unset, -1 follow motherboard, else percent.
    public int DesiredDuty = -2;
    public long DesiredDutyAtMs;
    public int AppliedDuty = -3;
    public string SentEffectIndex = "";
    // Live frames went out after the last stored upload, so the receiver shows them, not the stored look.
    public bool LiveSinceUpload;
    public bool SaveDirty;
    public long LastUploadMs;
    public long LcdRestartUntilMs;

    public FlexUnitLink(FlexUnitPort port, IFlexReceiverTransport transport)
    {
        Port = port;
        Transport = transport;
    }

    public void Dispose() => Transport.Dispose();
}

/// <summary>
/// Owns the Flex receivers: discovery results in, per-unit state out. Receiver traffic is
/// serialized per unit; the published <see cref="Units"/> array is replaced whole.
/// </summary>
public sealed class FlexHub : IDisposable
{
    public const int PollPeriodMs = 750;
    public const int ClockPeriodMs = 3000;
    public const int SaveSettleMs = 10_000;
    public const int PackagePacingMs = 12;
    private const int ReplyTimeoutMs = 100;
    private const int MissedBeforeStale = 3;
    private const int MissedBeforeDrop = 12;
    private const int LiveQuietForClockMs = 4000;

    private readonly IFlexDiscovery _discovery;
    private readonly Func<FlexUnitPort, IFlexReceiverTransport> _factory;
    private readonly Func<long> _nowMs;
    private readonly Func<DateTime> _localNow;
    private readonly Action<int> _sleep;
    private readonly object _linksLock = new();
    private readonly Dictionary<string, FlexUnitLink> _links = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, long> _failedPaths = new(StringComparer.OrdinalIgnoreCase);
    private const int RetryOpenMs = 10_000;
    private readonly long _startMs;
    private FlexUnit[] _units = Array.Empty<FlexUnit>();

    public FlexHub(
        IFlexDiscovery discovery,
        Func<FlexUnitPort, IFlexReceiverTransport> factory,
        Func<long>? nowMs = null,
        Func<DateTime>? localNow = null,
        Action<int>? sleep = null)
    {
        _discovery = discovery;
        _factory = factory;
        _nowMs = nowMs ?? (() => Environment.TickCount64);
        _localNow = localNow ?? (() => DateTime.Now);
        _sleep = sleep ?? Thread.Sleep;
        _startMs = _nowMs();
    }

    public FlexUnit[] Units => Volatile.Read(ref _units);

    public event Action? UnitsChanged;

    public bool IsConnected => Units.Length > 0;

    /// <summary>ok with units; busy when a receiver was found but would not open (another app holds it); none otherwise.</summary>
    public string LinkStatus
    {
        get
        {
            if (Units.Length > 0)
            {
                return LianLiWireless.Slv3LinkStatus.Ok;
            }
            lock (_linksLock)
            {
                return _failedPaths.Count > 0 ? LianLiWireless.Slv3LinkStatus.Busy : LianLiWireless.Slv3LinkStatus.None;
            }
        }
    }

    public FlexUnit? Find(string mac) => Units.FirstOrDefault(u => string.Equals(u.Mac, mac, StringComparison.OrdinalIgnoreCase));

    /// <summary>Opens new receivers, drops departed ones, and publishes the unit list.</summary>
    public void Refresh()
    {
        IReadOnlyList<FlexUnitPort> ports;
        try
        {
            ports = _discovery.Discover();
        }
        catch (Exception)
        {
            ports = Array.Empty<FlexUnitPort>();
        }
        var present = new HashSet<string>(ports.Select(p => p.ReceiverPath), StringComparer.OrdinalIgnoreCase);
        var changed = false;
        var toOpen = new List<FlexUnitPort>();
        lock (_linksLock)
        {
            foreach (var mac in _links.Where(kv => !present.Contains(kv.Value.Port.ReceiverPath)).Select(kv => kv.Key).ToList())
            {
                _links[mac].Dispose();
                _links.Remove(mac);
                changed = true;
            }
            foreach (var stale in _failedPaths.Keys.Where(p => !present.Contains(p)).ToList())
            {
                _failedPaths.Remove(stale);
            }
            foreach (var port in ports)
            {
                if (_links.Values.Any(l => string.Equals(l.Port.ReceiverPath, port.ReceiverPath, StringComparison.OrdinalIgnoreCase)))
                {
                    continue;
                }
                if (_failedPaths.TryGetValue(port.ReceiverPath, out var failedAt) && _nowMs() - failedAt < RetryOpenMs)
                {
                    continue;
                }
                toOpen.Add(port);
            }
        }
        // Opening talks to the device, so it runs outside the lock the other threads read links under.
        foreach (var port in toOpen)
        {
            var link = TryOpen(port);
            lock (_linksLock)
            {
                if (link?.Info is { } info && !_links.ContainsKey(info.Mac))
                {
                    if (_wantedDuty.TryGetValue(info.Mac.ToUpperInvariant(), out var wanted))
                    {
                        link.DesiredDuty = wanted;
                    }
                    _links[info.Mac] = link;
                    changed = true;
                }
                else
                {
                    link?.Dispose();
                    _failedPaths[port.ReceiverPath] = _nowMs();
                }
            }
        }
        if (changed)
        {
            Publish();
            UnitsChanged?.Invoke();
        }
    }

    private FlexUnitLink? TryOpen(FlexUnitPort port)
    {
        IFlexReceiverTransport transport;
        try
        {
            transport = _factory(port);
        }
        catch (Exception ex)
        {
            ServiceLog.Warn($"[lianli-flex] open failed for {port.ReceiverPath}: {ex.Message}");
            return null;
        }
        var link = new FlexUnitLink(port, transport);
        PollLocked(link);
        if (link.Info is null)
        {
            link.Dispose();
            return null;
        }
        link.FirmwareVersion = ReadVersion(link);
        return link;
    }

    private int ReadVersion(FlexUnitLink link)
    {
        lock (link.Io)
        {
            var reply = Exchange(link, FlexProtocol.BuildGetVer(), FlexProtocol.CmdGetVer);
            return reply is null ? 0 : FlexProtocol.ParseVersion(reply);
        }
    }

    private byte[]? Exchange(FlexUnitLink link, byte[] frame, byte expect)
    {
        if (!FlexProtocol.IsAllowed(frame))
        {
            throw new InvalidOperationException("frame is not on the receiver allow list");
        }
        link.Transport.DiscardReplies();
        if (!link.Transport.Write(frame))
        {
            return null;
        }
        var deadline = _nowMs() + ReplyTimeoutMs;
        while (true)
        {
            var reply = link.Transport.Read(ReplyTimeoutMs);
            if (reply is null)
            {
                return null;
            }
            if (reply[0] == expect)
            {
                return reply;
            }
            if (_nowMs() > deadline)
            {
                return null;
            }
        }
    }

    private bool Send(FlexUnitLink link, byte[] frame)
    {
        if (!FlexProtocol.IsAllowed(frame))
        {
            throw new InvalidOperationException("frame is not on the receiver allow list");
        }
        return link.Transport.Write(frame);
    }

    // Skips the pass while an upload or another exchange holds the receiver.
    private void TryPoll(FlexUnitLink link)
    {
        if (!Monitor.TryEnter(link.Io))
        {
            return;
        }
        try
        {
            PollOnce(link);
        }
        finally
        {
            Monitor.Exit(link.Io);
        }
    }

    private void PollLocked(FlexUnitLink link)
    {
        lock (link.Io)
        {
            PollOnce(link);
        }
    }

    private void PollOnce(FlexUnitLink link)
    {
        var reply = Exchange(link, FlexProtocol.BuildGetInfo(), FlexProtocol.CmdGetInfo);
        var info = reply is null ? null : FlexProtocol.ParseInfo(reply);
        link.LastPollMs = _nowMs();
        if (info is null)
        {
            link.Missed++;
            return;
        }
        link.Missed = 0;
        link.Info = info;
    }

    /// <summary>One worker pass: poll, clock sync, duty, flash save, and a republish when anything changed.</summary>
    public void Tick()
    {
        FlexUnitLink[] links;
        lock (_linksLock)
        {
            links = _links.Values.ToArray();
        }
        var now = _nowMs();
        var dropped = false;
        foreach (var link in links)
        {
            if (now - link.LastPollMs >= PollPeriodMs)
            {
                TryPoll(link);
            }
            if (link.Missed >= MissedBeforeDrop)
            {
                lock (_linksLock)
                {
                    if (link.Info is { } gone)
                    {
                        _links.Remove(gone.Mac);
                    }
                }
                link.Dispose();
                dropped = true;
                continue;
            }
            if (link.Info is { RfBound: false } && now - link.LastClockMs >= ClockPeriodMs && now - link.LastLiveMs >= LiveQuietForClockMs && Monitor.TryEnter(link.Io))
            {
                try
                {
                    link.LastClockMs = now;
                    Send(link, FlexProtocol.BuildSyncClock((now - _startMs) / 1000.0));
                }
                finally
                {
                    Monitor.Exit(link.Io);
                }
            }
            ApplyDuty(link);
            TrySave(link);
        }
        Publish();
        if (dropped)
        {
            UnitsChanged?.Invoke();
        }
    }

    private void ApplyDuty(FlexUnitLink link)
    {
        var want = link.DesiredDuty;
        if (want == -2 || want == link.AppliedDuty || link.Info is not { RfBound: false } info)
        {
            return;
        }
        var family = link.Port.Family;
        var frame = want == -1
            ? FlexProtocol.BuildFollowMotherboard()
            : FlexProtocol.BuildSetPwm(FlexProtocol.EncodeDuty(family, want));
        // An upload holds the receiver; the wanted duty stays set and goes out on the next pass.
        if (!Monitor.TryEnter(link.Io))
        {
            return;
        }
        try
        {
            if (Send(link, frame))
            {
                link.AppliedDuty = want;
            }
        }
        finally
        {
            Monitor.Exit(link.Io);
        }
    }

    private void TrySave(FlexUnitLink link)
    {
        if (!link.SaveDirty)
        {
            return;
        }
        if (!Monitor.TryEnter(link.Io))
        {
            return;
        }
        try
        {
            // Checked again under the lock: another pass may have saved, or an upload may have restarted the wait.
            if (!link.SaveDirty || _nowMs() - link.LastUploadMs < SaveSettleMs)
            {
                return;
            }
            link.SaveDirty = false;
            Send(link, FlexProtocol.BuildSave());
        }
        finally
        {
            Monitor.Exit(link.Io);
        }
    }

    private void Publish()
    {
        FlexUnitLink[] links;
        lock (_linksLock)
        {
            links = _links.Values.ToArray();
        }
        var now = _nowMs();
        var units = new List<FlexUnit>(links.Length);
        foreach (var link in links)
        {
            if (link.Info is not { } info)
            {
                continue;
            }
            units.Add(new FlexUnit
            {
                Mac = info.Mac,
                Family = link.Port.Family,
                Info = info,
                FirmwareVersion = link.FirmwareVersion,
                ScreenSerial = link.Port.ScreenSerial,
                Stale = link.Missed >= MissedBeforeStale,
                LcdRestarting = now < link.LcdRestartUntilMs,
            });
        }
        units.Sort((a, b) => string.CompareOrdinal(a.Mac, b.Mac));
        Volatile.Write(ref _units, units.ToArray());
    }

    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, int> _wantedDuty = new(StringComparer.Ordinal);

    private FlexUnitLink? LinkOf(string mac)
    {
        lock (_linksLock)
        {
            return _links.TryGetValue(mac, out var l) ? l : null;
        }
    }

    /// <summary>Wanted duty percent, or null to follow the motherboard header; written when it changes.</summary>
    public void SetDuty(string mac, int? percent)
    {
        var wanted = percent ?? -1;
        // Kept per receiver, not per connection, so a replug or a reopen brings the fan back to it.
        _wantedDuty[mac.ToUpperInvariant()] = wanted;
        if (LinkOf(mac) is { } link)
        {
            link.DesiredDuty = wanted;
            link.DesiredDutyAtMs = _nowMs();
        }
    }

    /// <summary>How long a requested duty is reported before the receiver's own echo is trusted again.</summary>
    public const int PendingDutyMs = 3000;

    /// <summary>
    /// The duty (percent, or -1 for follow-motherboard) the caller asked for while the receiver's echo has not
    /// caught up with it yet, so a control card does not flip back to the old state between the write and the
    /// next poll; null once the echo matches or after <see cref="PendingDutyMs"/>.
    /// </summary>
    public int? PendingDuty(string mac)
    {
        if (LinkOf(mac) is not { } link || link.DesiredDuty == -2 || link.Info is not { } info)
        {
            return null;
        }
        if (_nowMs() - link.DesiredDutyAtMs >= PendingDutyMs)
        {
            return null;
        }
        var echo = info.Pwm.Length > 0 ? info.Pwm[0] : -1;
        var expected = link.DesiredDuty == -1 ? FlexProtocol.PwmFollowMotherboard : FlexProtocol.EncodeDuty(link.Port.Family, link.DesiredDuty);
        return echo == expected ? null : link.DesiredDuty;
    }

    /// <summary>Hands the receiver's lighting to the motherboard ARGB input (true) or back to USB. Never sent to an RF-bound receiver.</summary>
    public bool SetMotherboardArgb(string mac, bool on)
    {
        if (LinkOf(mac) is not { } link || link.Info is not { RfBound: false })
        {
            return false;
        }
        if (!Monitor.TryEnter(link.Io))
        {
            return false;
        }
        try
        {
            return Send(link, FlexProtocol.BuildSyncBoard(on));
        }
        finally
        {
            Monitor.Exit(link.Io);
        }
    }

    /// <summary>Sends one live RGB frame (R,G,B per LED). Dropped when an upload holds the receiver.</summary>
    public bool SendLive(string mac, ReadOnlySpan<byte> rgb)
    {
        if (LinkOf(mac) is not { } link || link.Info is not { RfBound: false })
        {
            return false;
        }
        if (!Monitor.TryEnter(link.Io))
        {
            return false;
        }
        try
        {
            link.LastLiveMs = _nowMs();
            link.LiveSinceUpload = true;
            foreach (var packet in FlexProtocol.BuildLivePackets(rgb))
            {
                if (!Send(link, packet))
                {
                    return false;
                }
            }
            return true;
        }
        finally
        {
            Monitor.Exit(link.Io);
        }
    }

    /// <summary>Uploads a stored animation and, unless <paramref name="saveToFlash"/> is off (the receiver already holds it), arms the flash save. False when any packet fails.</summary>
    public bool UploadPackage(string mac, byte[] compressed, int frames, int ledCount, double intervalMs, bool saveToFlash = true)
    {
        if (LinkOf(mac) is not { } link || link.Info is not { RfBound: false })
        {
            return false;
        }
        var index = FlexProtocol.BuildEffectIndex(_localNow());
        var packets = FlexProtocol.BuildPackagePackets(index, compressed, frames, ledCount, intervalMs);
        lock (link.Io)
        {
            foreach (var p in packets)
            {
                if (!Send(link, p))
                {
                    return false;
                }
                _sleep(PackagePacingMs);
            }
            if (!Send(link, FlexProtocol.BuildEnable()))
            {
                return false;
            }
            link.SentEffectIndex = Convert.ToHexString(index);
            link.LastUploadMs = _nowMs();
            link.SaveDirty |= saveToFlash;
            link.LiveSinceUpload = false;
        }
        return true;
    }

    /// <summary>True when live frames were sent after the unit's last stored upload.</summary>
    public bool LiveSinceUpload(string mac) => LinkOf(mac)?.LiveSinceUpload ?? false;

    /// <summary>The effect index this process last uploaded to the unit, or empty.</summary>
    public string SentEffectIndex(string mac) => LinkOf(mac)?.SentEffectIndex ?? "";

    /// <summary>Ends a restart window once the screen is back.</summary>
    public void EndLcdRestart(string mac)
    {
        if (LinkOf(mac) is { } link)
        {
            link.LcdRestartUntilMs = 0;
            Publish();
        }
    }

    /// <summary>Reboots the screen controller through the receiver; the caller re-attaches the screen.</summary>
    public bool RebootLcd(string mac, int holdMs)
    {
        if (LinkOf(mac) is not { } link)
        {
            return false;
        }
        lock (link.Io)
        {
            if (!Send(link, FlexProtocol.BuildRebootLcd()))
            {
                return false;
            }
            link.LcdRestartUntilMs = _nowMs() + holdMs;
            link.AppliedDuty = -3;
        }
        Publish();
        return true;
    }

    /// <summary>Closes every receiver and publishes no units (gate off or shutdown).</summary>
    public void Close()
    {
        bool had;
        lock (_linksLock)
        {
            had = _links.Count > 0;
            foreach (var l in _links.Values)
            {
                l.Dispose();
            }
            _links.Clear();
            _failedPaths.Clear();
        }
        Volatile.Write(ref _units, Array.Empty<FlexUnit>());
        if (had)
        {
            UnitsChanged?.Invoke();
        }
    }

    public void Dispose()
    {
        lock (_linksLock)
        {
            foreach (var l in _links.Values)
            {
                l.Dispose();
            }
            _links.Clear();
        }
        Volatile.Write(ref _units, Array.Empty<FlexUnit>());
    }
}
