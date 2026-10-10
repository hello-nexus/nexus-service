using System;
using Nexus.Service.Models.Cooling;

namespace Nexus.Service.Cooling;

/// <summary>
/// A fan's current <see cref="FanControlBlocks"/> value. Every change is reported, because no
/// route mutation accompanies it: the provider pushes "cooling" so open pages refetch.
/// </summary>
public sealed class FanControlBlock(Action changed)
{
    private volatile string? _value;

    /// <summary>Lock-free: every fan-channel read takes it.</summary>
    public string? Value => _value;

    /// <summary>Callers serialize writes.</summary>
    public void Set(string? value)
    {
        if (_value == value) return;
        _value = value;
        changed();
    }
}
