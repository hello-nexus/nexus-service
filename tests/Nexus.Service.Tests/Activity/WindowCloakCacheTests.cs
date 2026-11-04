using System;
using System.Collections.Generic;
using Nexus.Service.Activity;
using Xunit;

namespace Nexus.Service.Tests.Activity;

public class WindowCloakCacheTests
{
    private readonly Dictionary<long, bool> _truth = new();
    private int _reads;

    private WindowCloakCache Make(int every = 12, int max = 16, bool live = true)
    {
        Func<long, bool> read = h => { _reads++; return _truth.GetValueOrDefault(h); };
        return new WindowCloakCache(read, every, max) { EventsLive = live };
    }

    private static bool Ask(WindowCloakCache c, long h, int right = 100) => c.IsCloaked(h, 0, 0, right, 100);

    private void Pass(WindowCloakCache c, params long[] hwnds)
    {
        c.BeginPass();
        foreach (var h in hwnds) Ask(c, h);
        c.EndPass();
    }

    [Fact]
    public void FirstSight_Reads_ThenServesFromCache()
    {
        var c = Make();
        _truth[1] = true;
        Pass(c, 1);
        Assert.Equal(1, _reads);
        c.BeginPass();
        Assert.True(Ask(c, 1));
        Assert.Equal(0, c.Reads);
        Assert.Equal(1, _reads);
    }

    [Fact]
    public void MarkDirty_ForcesReread_AndPicksUpNewState()
    {
        var c = Make();
        _truth[1] = true;
        Pass(c, 1);
        _truth[1] = false;
        c.MarkDirty(1);
        c.BeginPass();
        Assert.False(Ask(c, 1));
        Assert.Equal(1, c.Reads);
    }

    [Fact]
    public void UncachedState_IsNotChanged_WithoutAnEvent()
    {
        var c = Make();
        _truth[1] = true;
        Pass(c, 1);
        _truth[1] = false;
        c.BeginPass();
        Assert.True(Ask(c, 1));
    }

    [Fact]
    public void Evict_ForgetsHandle_SoReuseIsReadFresh()
    {
        var c = Make();
        _truth[1] = true;
        Pass(c, 1);
        c.Evict(1);
        _truth[1] = false;
        c.BeginPass();
        Assert.False(Ask(c, 1));
        Assert.Equal(1, c.Reads);
    }

    [Fact]
    public void EntryNotSeenInAPass_IsEvicted()
    {
        var c = Make();
        _truth[1] = true;
        Pass(c, 1);
        Pass(c);
        Assert.Equal(0, c.Count);
        _truth[1] = false;
        c.BeginPass();
        Assert.False(Ask(c, 1));
    }

    [Fact]
    public void Gap_InCandidacy_ForcesReread()
    {
        var c = Make();
        _truth[1] = true;
        Pass(c, 1);
        c.BeginPass();
        c.EndPass();
        _truth[1] = false;
        c.BeginPass();
        Assert.False(Ask(c, 1));
    }

    [Fact]
    public void CloakedEntry_Rereads_WhenRectMoves_ButUncloakedDoesNot()
    {
        var c = Make();
        _truth[1] = true;
        _truth[2] = false;
        c.BeginPass(); Ask(c, 1); Ask(c, 2); c.EndPass();
        c.BeginPass(); Ask(c, 1, right: 200); Ask(c, 2, right: 200);
        Assert.Equal(1, c.Reads);
    }

    [Fact]
    public void Trickle_RefreshesOldEntries_WithinBudgetPerPass()
    {
        var c = Make(every: 4, max: 2);
        var all = new long[16];
        for (var i = 0; i < all.Length; i++) { all[i] = i + 1; _truth[i + 1] = true; }
        Pass(c, all);
        var maxReadsInPass = 0;
        var refreshed = new HashSet<long>();
        for (var p = 0; p < 12; p++)
        {
            c.BeginPass();
            foreach (var h in all) Ask(c, h);
            c.EndPass();
            maxReadsInPass = System.Math.Max(maxReadsInPass, c.Reads);
        }
        Assert.True(maxReadsInPass <= 2, $"burst of {maxReadsInPass} reads");
        Assert.True(_reads > 16, "trickle never refreshed anything");
    }

    [Fact]
    public void TrickleDisabled_NeverRereadsATrustedEntry_AndCountsNone()
    {
        var c = Make(every: 2, max: 4);
        c.TrickleEnabled = false;
        _truth[1] = true;
        Pass(c, 1);
        _truth[1] = false;
        for (var p = 0; p < 8; p++)
        {
            c.BeginPass();
            Assert.True(Ask(c, 1));
            c.EndPass();
            Assert.Equal(0, c.Trickles);
        }
        Assert.Equal(1, _reads);
    }

    [Fact]
    public void Trickles_CountsPerPass()
    {
        var c = Make(every: 2, max: 4);
        _truth[1] = true;
        Pass(c, 1);
        var total = 0;
        for (var p = 0; p < 6; p++)
        {
            c.BeginPass();
            Ask(c, 1);
            c.EndPass();
            total += c.Trickles;
        }
        Assert.True(total > 0);
    }

    [Fact]
    public void Trickle_PicksUpMissedEvent_Eventually()
    {
        var c = Make(every: 3, max: 4);
        _truth[1] = true;
        Pass(c, 1);
        _truth[1] = false;
        var seen = true;
        for (var p = 0; p < 6 && seen; p++)
        {
            c.BeginPass();
            seen = Ask(c, 1);
            c.EndPass();
        }
        Assert.False(seen);
    }

    [Fact]
    public void WithoutLiveEvents_EveryAsk_ReadsDwm()
    {
        var c = Make(live: false);
        _truth[1] = true;
        Pass(c, 1);
        _truth[1] = false;
        c.BeginPass();
        Assert.False(Ask(c, 1));
        Assert.Equal(1, c.Reads);
        c.EndPass();
        c.BeginPass();
        Ask(c, 1);
        Assert.Equal(1, c.Reads);
    }

    [Fact]
    public void Clear_DropsTrust_SoTheNextPassRereads()
    {
        var c = Make();
        _truth[1] = true;
        Pass(c, 1);
        c.Clear();
        _truth[1] = false;
        c.BeginPass();
        Assert.False(Ask(c, 1));
        Assert.Equal(1, c.Reads);
        Assert.Equal(1, c.Count);
    }

    [Fact]
    public void ResumeAfterPause_DoesNotServeAPreGapEntry()
    {
        var c = Make();
        _truth[1] = true;
        Pass(c, 1);
        _truth[1] = false;
        // a paused poller runs no passes, so pass counting alone would trust the entry
        c.Clear();
        c.BeginPass();
        Assert.False(Ask(c, 1));
    }

    [Fact]
    public void Read_RunsOutsideTheLock_AndEventDuringReadMarksEntryDirty()
    {
        WindowCloakCache? c = null;
        var calls = 0;
        c = new WindowCloakCache(h =>
        {
            calls++;
            // re-entering the cache from the read would deadlock if the lock were held
            _ = c!.Count;
            if (calls == 1) c.MarkDirty(h);
            return calls == 1;
        }) { EventsLive = true };
        c.BeginPass();
        Assert.True(c.IsCloaked(1, 0, 0, 100, 100));
        c.EndPass();
        c.BeginPass();
        Assert.False(c.IsCloaked(1, 0, 0, 100, 100));
        Assert.Equal(2, calls);
    }

    [Fact]
    public void DestroyDuringRead_DoesNotLeaveATrustedEntry()
    {
        WindowCloakCache? c = null;
        var calls = 0;
        c = new WindowCloakCache(h => { calls++; if (calls == 1) c!.Evict(h); return calls == 1; }) { EventsLive = true };
        c.BeginPass();
        c.IsCloaked(1, 0, 0, 100, 100);
        c.EndPass();
        c.BeginPass();
        Assert.False(c.IsCloaked(1, 0, 0, 100, 100));
    }

    [Fact]
    public void WithoutEvents_EntriesAreNotServed()
    {
        var c = Make(live: false);
        _truth[1] = true;
        Pass(c, 1);
        c.BeginPass();
        Ask(c, 1);
        Assert.Equal(1, c.Reads);
    }

    [Fact]
    public void ServeWithoutEvents_ServesStoredEntries_AndTrickleStillRefreshes()
    {
        var c = Make(every: 2, max: 4, live: false);
        c.ServeWithoutEvents = true;
        _truth[1] = true;
        Pass(c, 1);
        c.BeginPass();
        Assert.True(Ask(c, 1));
        Assert.Equal(0, c.Reads);
        c.EndPass();
        _truth[1] = false;
        var seen = true;
        for (var p = 0; p < 6 && seen; p++)
        {
            c.BeginPass();
            seen = Ask(c, 1);
            c.EndPass();
        }
        Assert.False(seen);
    }

    [Fact]
    public void StoreReadsOff_NeverRemembersAFakeRead_AndFlippingDropsEntries()
    {
        var c = Make();
        _truth[1] = true;
        Pass(c, 1);
        c.StoreReads = false;
        _truth[1] = false;
        for (var p = 0; p < 3; p++)
        {
            c.BeginPass();
            Assert.False(Ask(c, 1));
            Assert.Equal(1, c.Reads);
            c.EndPass();
        }
        Assert.Equal(0, c.Count);
        c.StoreReads = true;
        c.BeginPass();
        Assert.False(Ask(c, 1));
        c.EndPass();
        c.BeginPass();
        Ask(c, 1);
        Assert.Equal(0, c.Reads);
    }

    [Fact]
    public void NoteSkippedPasses_ClearsOnEachTransition_ButNotWhileSteady()
    {
        var c = Make();
        _truth[1] = true;
        Pass(c, 1);
        c.NoteSkippedPasses(false);
        Assert.Equal(1, c.Count);
        c.NoteSkippedPasses(true);
        Assert.Equal(0, c.Count);
        Pass(c, 1);
        c.NoteSkippedPasses(true);
        Assert.Equal(1, c.Count);
        c.NoteSkippedPasses(false);
        Assert.Equal(0, c.Count);
    }
}
