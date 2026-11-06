using System;
using System.Collections.Concurrent;

namespace Nexus.Service.Peripherals.FanLcd;

/// <summary>
/// A fan screen's panel record rotation, by screen serial. The record id comes from a file read, so a
/// found id is kept for <see cref="IdTtlMs"/>. A read that finds nothing (store unreadable, record not
/// minted yet) keeps the last answer and is never cached, so the next call looks again and the
/// screen's turn does not snap back on a bad read.
/// </summary>
public sealed class FanLcdPanelRotationSource
{
    /// <summary>How long a screen's panel record id is trusted before the stream store is read again.</summary>
    public const long IdTtlMs = 5000;

    private readonly Func<string, string?> _panelIdOf;
    private readonly Func<string, int?> _rotationOf;
    private readonly Func<long> _clockMs;
    private readonly ConcurrentDictionary<string, (string? Id, long ExpiresMs, int Rotation)> _known = new(StringComparer.OrdinalIgnoreCase);

    /// <param name="panelIdOf">The panel record id of a screen, or null when none is found.</param>
    /// <param name="rotationOf">The record's rotation, or null when the record is gone.</param>
    public FanLcdPanelRotationSource(Func<string, string?> panelIdOf, Func<string, int?> rotationOf, Func<long>? clockMs = null)
    {
        _panelIdOf = panelIdOf;
        _rotationOf = rotationOf;
        _clockMs = clockMs ?? (() => Environment.TickCount64);
    }

    public int RotationOf(string serial)
    {
        _known.TryGetValue(serial, out var known);
        var now = _clockMs();
        if ((known.Id is null || known.ExpiresMs <= now) && _panelIdOf(serial) is { Length: > 0 } id)
        {
            known = (id, now + IdTtlMs, known.Rotation);
        }
        if (known.Id is not null && _rotationOf(known.Id) is { } rotation)
        {
            known.Rotation = rotation;
        }
        _known[serial] = known;
        return known.Rotation;
    }
}
