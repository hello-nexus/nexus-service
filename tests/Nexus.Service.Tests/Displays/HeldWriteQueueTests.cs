using System.Collections.Generic;
using System.Linq;
using Nexus.Service.Platform.Displays;
using Xunit;

namespace Nexus.Service.Tests.Displays;

public class HeldWriteQueueTests
{
    private const int Max = 3;
    private const string Id = "display-a";

    private static HeldWrite W(byte code, int value, string id = Id) => new(id, code, value);

    private static (HeldWriteQueue Queue, List<HeldWrite> Written, HashSet<byte> Failing) Build(params HeldWrite[] held)
    {
        var queue = new HeldWriteQueue(Max);
        foreach (var w in held)
        {
            queue.Hold(w);
        }
        return (queue, new List<HeldWrite>(), new HashSet<byte>());
    }

    private static bool Write(List<HeldWrite> written, HashSet<byte> failing, HeldWrite w)
    {
        written.Add(w);
        return !failing.Contains(w.Code!.Value);
    }

    [Fact]
    public void Replay_applies_in_the_order_first_held()
    {
        var (queue, written, failing) = Build(W(1, 10), W(2, 20), W(3, 30));

        var result = queue.Replay(w => Write(written, failing, w), countAttempt: true);

        Assert.Equal(new byte?[] { 1, 2, 3 }, written.Select(w => w.Code).ToArray());
        Assert.Equal(3, result.Applied);
        Assert.Equal(0, queue.Count);
    }

    [Fact]
    public void Replace_in_place_keeps_the_original_position()
    {
        var (queue, written, failing) = Build(W(1, 10), W(2, 20));
        queue.Hold(W(1, 11));

        queue.Replay(w => Write(written, failing, w), countAttempt: true);

        Assert.Equal(new[] { (byte)1, (byte)2 }, written.Select(w => w.Code!.Value).ToArray());
        Assert.Equal(11, written[0].Value);
    }

    [Fact]
    public void A_failed_head_keeps_later_entries_for_that_display_held_in_order()
    {
        var (queue, written, failing) = Build(W(1, 10), W(2, 20), W(3, 30));
        failing.Add(1);

        queue.Replay(w => Write(written, failing, w), countAttempt: true);

        Assert.Equal(new byte?[] { 1 }, written.Select(w => w.Code).ToArray());
        Assert.Equal(3, queue.Count);

        failing.Clear();
        written.Clear();
        queue.Replay(w => Write(written, failing, w), countAttempt: true);

        Assert.Equal(new byte?[] { 1, 2, 3 }, written.Select(w => w.Code).ToArray());
    }

    [Fact]
    public void A_failed_head_does_not_block_another_display()
    {
        var (queue, written, failing) = Build(W(1, 10), W(2, 20, "display-b"));
        failing.Add(1);

        queue.Replay(w => Write(written, failing, w), countAttempt: true);

        Assert.Contains(written, w => w.Id == "display-b");
    }

    [Fact]
    public void A_new_write_for_a_blocked_display_is_queued_not_written()
    {
        var (queue, written, failing) = Build(W(1, 10));
        failing.Add(1);

        var result = queue.Replay(w => Write(written, failing, w), countAttempt: false, incoming: W(2, 20));

        Assert.Equal(HeldIncoming.Queued, result.Incoming);
        Assert.DoesNotContain(written, w => w.Code == 2);
        Assert.Equal(2, queue.Count);

        failing.Clear();
        written.Clear();
        queue.Replay(w => Write(written, failing, w), countAttempt: true);
        Assert.Equal(new byte?[] { 1, 2 }, written.Select(w => w.Code).ToArray());
    }

    [Fact]
    public void A_new_write_replacing_a_held_one_for_a_blocked_display_takes_its_slot()
    {
        var (queue, written, failing) = Build(W(1, 10), W(2, 20), W(3, 30));
        failing.Add(1);

        var result = queue.Replay(w => Write(written, failing, w), countAttempt: false, incoming: W(2, 21));

        Assert.Equal(HeldIncoming.Queued, result.Incoming);
        failing.Clear();
        written.Clear();
        queue.Replay(w => Write(written, failing, w), countAttempt: true);
        Assert.Equal(new byte?[] { 1, 2, 3 }, written.Select(w => w.Code).ToArray());
        Assert.Equal(21, written[1].Value);
    }

    [Fact]
    public void A_new_write_takes_its_predecessors_slot_even_when_a_later_entry_fails()
    {
        // preset (1), gainR (2) is replaced by the new write, gainG (3) fails on replay.
        var (queue, written, failing) = Build(W(1, 10), W(2, 20), W(3, 30));
        failing.Add(3);

        var result = queue.Replay(w => Write(written, failing, w), countAttempt: false, incoming: W(2, 21));

        Assert.Equal(HeldIncoming.Written, result.Incoming);
        Assert.Equal(new byte?[] { 1, 2, 3 }, written.Select(w => w.Code).ToArray());
        Assert.Equal(21, written[1].Value);
        Assert.Equal(1, queue.Count);
    }

    [Fact]
    public void A_new_write_that_fails_in_its_slot_is_reported_as_a_failure_and_stays_held()
    {
        var (queue, written, failing) = Build(W(1, 10), W(2, 20));
        failing.Add(2);

        var result = queue.Replay(w => Write(written, failing, w), countAttempt: false, incoming: W(2, 21));

        Assert.Equal(HeldIncoming.HeldAfterFailure, result.Incoming);
        Assert.Equal(1, queue.Count);
        failing.Clear();
        written.Clear();
        queue.Replay(w => Write(written, failing, w), countAttempt: true);
        Assert.Equal(21, Assert.Single(written).Value);
    }

    [Fact]
    public void A_throw_while_the_new_write_has_no_predecessor_still_holds_the_new_write_last()
    {
        var (queue, written, failing) = Build(W(1, 10), W(2, 20));

        Assert.Throws<System.InvalidOperationException>(() => queue.Replay(
            w => w.Code == 1 ? throw new System.InvalidOperationException("boom") : Write(written, failing, w),
            countAttempt: false,
            incoming: W(9, 90)));

        written.Clear();
        queue.Replay(w => Write(written, failing, w), countAttempt: true);
        Assert.Equal(new byte?[] { 1, 2, 9 }, written.Select(w => w.Code).ToArray());
        Assert.Equal(90, written[2].Value);
    }

    [Fact]
    public void The_new_write_itself_throwing_is_held_in_its_slot_with_the_rest_behind_it()
    {
        var (queue, written, failing) = Build(W(1, 10), W(2, 20), W(3, 30));

        Assert.Throws<System.InvalidOperationException>(() => queue.Replay(
            w => w.Code == 2 ? throw new System.InvalidOperationException("boom") : Write(written, failing, w),
            countAttempt: false,
            incoming: W(2, 21)));

        Assert.Equal(2, queue.Count);
        written.Clear();
        queue.Replay(w => Write(written, failing, w), countAttempt: true);
        Assert.Equal(new byte?[] { 2, 3 }, written.Select(w => w.Code).ToArray());
        Assert.Equal(21, written[0].Value);
    }

    [Fact]
    public void A_predecessor_on_one_display_is_written_while_another_display_is_blocked()
    {
        var (queue, written, failing) = Build(W(1, 10, "display-b"), W(2, 20), W(3, 30, "display-b"));
        failing.Add(1);

        var result = queue.Replay(w => Write(written, failing, w), countAttempt: false, incoming: W(2, 21));

        Assert.Equal(HeldIncoming.Written, result.Incoming);
        Assert.Contains(written, w => w.Id == Id && w.Code == 2 && w.Value == 21);
        Assert.DoesNotContain(written, w => w.Id == "display-b" && w.Code == 3);
        Assert.Equal(2, queue.Count);
    }

    [Fact]
    public void Applied_counts_the_new_write_written_in_its_slot()
    {
        var (queue, written, failing) = Build(W(1, 10), W(2, 20));

        var result = queue.Replay(w => Write(written, failing, w), countAttempt: false, incoming: W(2, 21));

        Assert.Equal(2, result.Applied);
        Assert.Equal(2, result.Total);
    }

    [Fact]
    public void A_throwing_write_keeps_the_rest_of_the_queue_held_in_order()
    {
        var (queue, written, failing) = Build(W(1, 10), W(2, 20), W(3, 30));

        Assert.Throws<System.InvalidOperationException>(() => queue.Replay(
            w => w.Code == 2 ? throw new System.InvalidOperationException("boom") : Write(written, failing, w),
            countAttempt: true));

        Assert.Equal(2, queue.Count);
        written.Clear();
        queue.Replay(w => Write(written, failing, w), countAttempt: true);
        Assert.Equal(new byte?[] { 2, 3 }, written.Select(w => w.Code).ToArray());
    }

    [Fact]
    public void A_throwing_write_keeps_the_new_write_in_its_predecessors_slot()
    {
        var (queue, written, failing) = Build(W(1, 10), W(2, 20), W(3, 30));

        Assert.Throws<System.InvalidOperationException>(() => queue.Replay(
            w => w.Code == 1 ? throw new System.InvalidOperationException("boom") : Write(written, failing, w),
            countAttempt: false,
            incoming: W(3, 31)));

        written.Clear();
        queue.Replay(w => Write(written, failing, w), countAttempt: true);
        Assert.Equal(new byte?[] { 1, 2, 3 }, written.Select(w => w.Code).ToArray());
        Assert.Equal(31, written[2].Value);
    }

    [Fact]
    public void A_new_write_that_fails_in_its_predecessors_slot_is_held_there()
    {
        var (queue, written, failing) = Build(W(1, 10), W(2, 20), W(3, 30));
        failing.Add(2);

        var result = queue.Replay(w => Write(written, failing, w), countAttempt: false, incoming: W(2, 21));

        Assert.Equal(HeldIncoming.HeldAfterFailure, result.Incoming);
        failing.Clear();
        written.Clear();
        queue.Replay(w => Write(written, failing, w), countAttempt: true);
        Assert.Equal(new byte?[] { 2, 3 }, written.Select(w => w.Code).ToArray());
        Assert.Equal(21, written[0].Value);
    }

    [Fact]
    public void A_new_write_for_an_unblocked_display_is_left_for_the_caller()
    {
        var (queue, written, failing) = Build(W(1, 10));

        var result = queue.Replay(w => Write(written, failing, w), countAttempt: false, incoming: W(2, 20));

        Assert.Equal(HeldIncoming.NotHandled, result.Incoming);
        Assert.Equal(0, queue.Count);
    }

    [Fact]
    public void Replays_triggered_by_a_new_write_do_not_burn_attempts()
    {
        var (queue, written, failing) = Build(W(1, 10));
        failing.Add(1);

        for (var i = 0; i < 10; i++)
        {
            queue.Replay(w => Write(written, failing, w), countAttempt: false, incoming: W(2, 20 + i));
        }

        Assert.Equal(2, queue.Count);
        failing.Clear();
        written.Clear();
        queue.Replay(w => Write(written, failing, w), countAttempt: true);
        Assert.Equal(new byte?[] { 1, 2 }, written.Select(w => w.Code).ToArray());
    }

    [Fact]
    public void The_head_is_dropped_at_the_cap_and_the_entries_behind_it_proceed()
    {
        var (queue, written, failing) = Build(W(1, 10), W(2, 20));
        failing.Add(1);

        queue.Replay(w => Write(written, failing, w), countAttempt: true);
        queue.Replay(w => Write(written, failing, w), countAttempt: true);
        Assert.Equal(2, queue.Count);
        written.Clear();

        queue.Replay(w => Write(written, failing, w), countAttempt: true);

        Assert.Equal(new byte?[] { 1, 2 }, written.Select(w => w.Code).ToArray());
        Assert.Equal(0, queue.Count);
    }
}
