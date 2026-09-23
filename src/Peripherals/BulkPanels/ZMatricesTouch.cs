using System;
using System.Collections.Generic;

namespace Nexus.Service.Peripherals.BulkPanels;

public enum TouchPhase : byte
{
    Up = 1,
    Down = 2,
    Move = 3,
}

/// <summary>One contact report off the glass, in the panel's own coordinates.</summary>
public readonly record struct ZMatricesTouchReport(byte TrackId, TouchPhase Phase, ushort X, ushort Y);

/// <summary>
/// Touch reports on EP 0x81, interleaved with a 1 Hz ASCII status token. Two layouts, both
/// opening with 0xAE and carrying big-endian coordinates:
/// v1, 11 bytes: AE track event w h x y.
/// v2, 16 bytes: AE 5A 02 event track _ w h x y _ xor, where xor covers bytes 0-14.
/// </summary>
public sealed class ZMatricesTouchParser
{
    private const byte Header = 0xAE;
    private const byte V2Magic = 0x5A;
    private const byte V2Version = 2;
    private const int V1Length = 11;
    private const int V2Length = 16;

    private readonly byte[] _pending = new byte[128];
    private int _count;

    public void Feed(ReadOnlySpan<byte> data, List<ZMatricesTouchReport> reports)
    {
        if (data.Length > _pending.Length - _count)
        {
            _count = 0;
            if (data.Length > _pending.Length)
            {
                data = data[^_pending.Length..];
            }
        }
        data.CopyTo(_pending.AsSpan(_count));
        _count += data.Length;

        int at = 0;
        while (_count - at >= 2)
        {
            if (_pending[at] != Header)
            {
                at++;
                continue;
            }
            if (_pending[at + 1] == V2Magic)
            {
                if (_count - at < V2Length)
                {
                    break;
                }
                ParseV2(_pending.AsSpan(at, V2Length), reports);
                at += V2Length;
            }
            else
            {
                if (_count - at < V1Length)
                {
                    break;
                }
                ParseV1(_pending.AsSpan(at, V1Length), reports);
                at += V1Length;
            }
        }
        Buffer.BlockCopy(_pending, at, _pending, 0, _count - at);
        _count -= at;
    }

    private static void ParseV1(ReadOnlySpan<byte> p, List<ZMatricesTouchReport> reports) =>
        Add(p[1], p[2], BigEndian(p, 3), BigEndian(p, 5), BigEndian(p, 7), BigEndian(p, 9), reports);

    private static void ParseV2(ReadOnlySpan<byte> p, List<ZMatricesTouchReport> reports)
    {
        if (p[2] != V2Version)
        {
            return;
        }
        byte xor = 0;
        for (int i = 0; i < V2Length - 1; i++)
        {
            xor ^= p[i];
        }
        if (xor == p[V2Length - 1])
        {
            Add(p[4], p[3], BigEndian(p, 6), BigEndian(p, 8), BigEndian(p, 10), BigEndian(p, 12), reports);
        }
    }

    private static void Add(byte track, byte phase, ushort width, ushort height, ushort x, ushort y, List<ZMatricesTouchReport> reports)
    {
        if (phase is < 1 or > 3 || width == 0 || height == 0)
        {
            return;
        }
        reports.Add(new ZMatricesTouchReport(track, (TouchPhase)phase, x, y));
    }

    private static ushort BigEndian(ReadOnlySpan<byte> p, int at) => (ushort)((p[at] << 8) | p[at + 1]);

    /// <summary>
    /// Places a report on a landscape frame of the given size. The glass reports portrait
    /// coordinates in frame pixels: x runs up the short side, y along the long side.
    /// </summary>
    public static bool TryMapToFrame(ZMatricesTouchReport report, int width, int height, out int x, out int y)
    {
        x = 0;
        y = 0;
        if (report.X >= height || report.Y >= width)
        {
            return false;
        }
        x = report.Y;
        y = height - 1 - report.X;
        return true;
    }
}

/// <summary>
/// Turns glass reports into pointer injections: a repeated down releases the stale contact
/// first, a move with no contact down starts one, and an up with none down is dropped.
/// </summary>
public sealed class TouchContactTracker
{
    public readonly record struct Injection(uint PointerId, TouchPhase Phase, int X, int Y);

    private readonly Dictionary<uint, (int X, int Y)> _active = new();

    public void Apply(byte track, TouchPhase phase, int x, int y, bool mapped, List<Injection> output)
    {
        uint id = (uint)track + 1;
        bool down = _active.TryGetValue(id, out var last);
        if (phase == TouchPhase.Up)
        {
            if (down)
            {
                output.Add(new Injection(id, TouchPhase.Up, last.X, last.Y));
                _active.Remove(id);
            }
            return;
        }
        if (!mapped)
        {
            return;
        }
        if (phase == TouchPhase.Down && down)
        {
            output.Add(new Injection(id, TouchPhase.Up, last.X, last.Y));
            down = false;
        }
        if (down && (last.X, last.Y) == (x, y))
        {
            return;
        }
        output.Add(new Injection(id, down ? TouchPhase.Move : TouchPhase.Down, x, y));
        _active[id] = (x, y);
    }

    public void ReleaseAll(List<Injection> output)
    {
        foreach (var (id, at) in _active)
        {
            output.Add(new Injection(id, TouchPhase.Up, at.X, at.Y));
        }
        _active.Clear();
    }
}
