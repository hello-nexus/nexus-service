using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace Nexus.Service.Lighting.IdleDim;

/// <summary>One way to ask the desktop how long the session has been idle (Linux).</summary>
public interface IIdleTimeSource
{
    string Name { get; }

    /// <summary>Idle time in milliseconds, or null when this source does not answer here.</summary>
    Task<long?> TryGetIdleMsAsync();
}

/// <summary>
/// Picks the first source that answers, in priority order, and sticks with it
/// until it stops answering; then it walks the list again, so a desktop that
/// changes under the service (or one whose bus was not up yet) is picked up.
/// </summary>
public sealed class IdleTimeSourceSelector
{
    private readonly IReadOnlyList<IIdleTimeSource> _sources;
    private IIdleTimeSource? _active;

    public IdleTimeSourceSelector(IReadOnlyList<IIdleTimeSource> sources) => _sources = sources;

    /// <summary>Name of the source that answered last, null when none has.</summary>
    public string? ActiveName => _active?.Name;

    /// <summary>Idle milliseconds from the active source, probing the rest if it fails; null when none answers.</summary>
    public async Task<long?> GetIdleMsAsync()
    {
        var active = _active;
        if (active is not null)
        {
            var ms = await TryAsync(active);
            if (ms is not null)
            {
                return ms;
            }
            _active = null;
        }
        foreach (var source in _sources)
        {
            if (ReferenceEquals(source, active))
            {
                continue;
            }
            var ms = await TryAsync(source);
            if (ms is not null)
            {
                _active = source;
                return ms;
            }
        }
        return null;
    }

    private static async Task<long?> TryAsync(IIdleTimeSource source)
    {
        try
        {
            var ms = await source.TryGetIdleMsAsync();
            return ms is >= 0 ? ms : null;
        }
        catch
        {
            return null;
        }
    }
}
