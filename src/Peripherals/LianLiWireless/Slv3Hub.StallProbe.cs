#if DEV_TOOLS
using System;
using System.IO;
using Nexus.Service.Platform;

namespace Nexus.Service.Peripherals.LianLiWireless;

// Temporary dev-tools A/B probe for a persistent RX "nodata" stall. While the stall lasts,
// every few seconds it probes the TX with GetMac, and once the stall passes the configured
// age it applies ONE recovery action, rotating across stalls. Every line also goes to the
// link log. Config file (optional): "<afterMs> <letters>", letters from a b c n.
public sealed partial class Slv3Hub
{
    private const long StallProbeEveryMs = 10_000;
    private long _stallStartMs;
    private long _stallActionMs;
    private long _stallNextProbeMs;
    private string _stallActionName = "";
    private int _stallSeq;
    private int _stallReopensAtStart;
    private volatile bool _pauseWindowUploads;

    private static (long AfterMs, string Letters) ReadStallConfig()
    {
        try
        {
            var path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "Nexus", "slv3-stall.txt");
            if (File.Exists(path))
            {
                var parts = File.ReadAllText(path).Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length == 2 && long.TryParse(parts[0], out var after) && parts[1].Length > 0)
                {
                    return (after, parts[1]);
                }
            }
        }
        catch
        {
            // fall through to the defaults
        }
        return (30_000, "abcn");
    }

    private void StallLog(string text)
    {
        ServiceLog.Warn("[slv3-stall] " + text);
        Diag.Event("slv3-stall " + text);
    }

    // Caller holds _lock; called on every nodata poll.
    private void StallOnNoDataLocked(long nowMs)
    {
        if (_stallStartMs == 0)
        {
            _stallStartMs = nowMs;
            _stallActionMs = 0;
            _stallActionName = "";
            _stallNextProbeMs = nowMs + StallProbeEveryMs;
            _stallReopensAtStart = _rxRecoveries;
            StallLog($"#{_stallSeq + 1} nodata stall begins");
        }
        var age = nowMs - _stallStartMs;
        if (nowMs >= _stallNextProbeMs)
        {
            _stallNextProbeMs = nowMs + StallProbeEveryMs;
            StallLog($"#{_stallSeq + 1} age={age}ms txprobe={ProbeTxLocked()} rxRecoveries={_rxRecoveries - _stallReopensAtStart}");
        }
        var (afterMs, letters) = ReadStallConfig();
        if (_stallActionMs != 0 || age < afterMs)
        {
            return;
        }
        var action = letters[_stallSeq % letters.Length];
        _stallActionMs = nowMs;
        _stallActionName = action.ToString();
        StallLog($"#{_stallSeq + 1} age={age}ms action={action} txprobe={ProbeTxLocked()}");
        switch (action)
        {
            case 'a':
                _nextRxRecoveryMs = 0;
                BeginRxRecoveryLocked("stall probe: 0x15 via TX", resetViaTx: true);
                break;
            case 'b':
                _pauseWindowUploads = true;
                break;
            case 'c':
                ReopenTxLocked(FindPortLocked(Slv3DongleRole.Tx), nowMs);
                _nextRxRecoveryMs = 0;
                BeginRxRecoveryLocked("stall probe: reopen TX and RX", resetViaTx: false);
                break;
        }
    }

    // Caller holds _lock; called when a valid device list arrives.
    private void StallOnGoodReplyLocked(long nowMs)
    {
        if (_stallStartMs == 0)
        {
            return;
        }
        var total = nowMs - _stallStartMs;
        var sinceAction = _stallActionMs == 0 ? "none" : $"{nowMs - _stallActionMs}ms";
        StallLog($"#{++_stallSeq} good reply: stall={total}ms action={(_stallActionName.Length == 0 ? "none" : _stallActionName)} sinceAction={sinceAction} rxRecoveries={_rxRecoveries - _stallReopensAtStart}");
        _stallStartMs = 0;
        _pauseWindowUploads = false;
    }

    private string ProbeTxLocked()
    {
        if (_tx is not { IsOpen: true })
        {
            return "txClosed";
        }
        var t0 = Environment.TickCount64;
        if (!_tx.RfSend(Slv3Protocol.BuildGetMac(_channel)))
        {
            return "writeFail";
        }
        var reply = _tx.RfRead(Slv3Protocol.UsbPacketSize);
        var lat = Environment.TickCount64 - t0;
        return Slv3Protocol.TryParseGetMac(reply, out var mac, out var clock, out _)
            ? $"ok(mac={Convert.ToHexString(mac)},clock={clock},{lat}ms)"
            : $"noReply({lat}ms,len={reply.Length})";
    }
}
#endif
