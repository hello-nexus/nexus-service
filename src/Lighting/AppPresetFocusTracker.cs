using System;
using System.Collections.Generic;
using Nexus.Service.Persistence;

namespace Nexus.Service.Lighting;

/// <summary>
/// Decides which layout preset the focused app calls for. Split from
/// <see cref="AppPresetSwitcher"/> so the state machine is exercised without a
/// host, an engine, or a store.
/// </summary>
public sealed class AppPresetFocusTracker
{
    /// <summary>How long an app must hold focus before its preset is applied.
    /// Alt-tabbing through windows would otherwise repaint every device once
    /// per window.</summary>
    public static readonly TimeSpan Dwell = TimeSpan.FromSeconds(1.5);

    private string _candidate = "";
    private long _candidateSinceMs;

    // Preset this tracker activated, and the one that was active before it did.
    // The restore is dropped when the active preset no longer matches what we
    // applied: the user picked something else and owns the selection again.
    // _appliedPresetId doubles as the re-apply guard, so a binding added while
    // its app already holds focus still takes effect on the next poll.
    private string _appliedPresetId = "";
    private string? _restoreToPresetId;

    /// <summary>Drops any pending restore, for when every binding is gone -
    /// the preset it would restore is no longer connected to anything.</summary>
    public void Reset()
    {
        _candidate = "";
        _candidateSinceMs = 0;
        _appliedPresetId = "";
        _restoreToPresetId = null;
    }

    /// <summary>Preset id to activate, or null when nothing should change.</summary>
    public string? Decide(
        string focusedProcess,
        string? activePresetId,
        IReadOnlyList<IAppBoundPreset> presets,
        long nowMs)
    {
        // No focus reported (provider unavailable, or nothing focused) holds
        // the current state rather than reading as "an unbound app".
        if (string.IsNullOrWhiteSpace(focusedProcess))
        {
            return null;
        }

        if (focusedProcess != _candidate)
        {
            _candidate = focusedProcess;
            _candidateSinceMs = nowMs;
            return null;
        }
        if (nowMs - _candidateSinceMs < (long)Dwell.TotalMilliseconds)
        {
            return null;
        }

        var target = FindPresetFor(focusedProcess, presets);
        if (target is not null)
        {
            // Already applied: a manual pick made since then owns the
            // selection, so this must not re-activate over it.
            if (_appliedPresetId == target.Id)
            {
                return null;
            }
            if (_appliedPresetId.Length == 0)
            {
                _restoreToPresetId = activePresetId;
            }
            _appliedPresetId = target.Id;
            return target.Id == activePresetId ? null : target.Id;
        }

        if (_appliedPresetId.Length == 0)
        {
            return null;
        }

        var userTookOver = activePresetId != _appliedPresetId;
        var restore = _restoreToPresetId;
        _appliedPresetId = "";
        _restoreToPresetId = null;
        if (userTookOver || restore is null || restore == activePresetId)
        {
            return null;
        }
        return Contains(presets, restore) ? restore : null;
    }

    private static bool Contains(IReadOnlyList<IAppBoundPreset> presets, string id)
    {
        foreach (var preset in presets)
        {
            if (preset.Id == id)
            {
                return true;
            }
        }
        return false;
    }

    private static IAppBoundPreset? FindPresetFor(string focusedProcess, IReadOnlyList<IAppBoundPreset> presets)
    {
        foreach (var preset in presets)
        {
            if (preset.Apps is null)
            {
                continue;
            }
            foreach (var app in preset.Apps)
            {
                if (AppPresetMatching.Matches(app.ProcessName, app.Name, focusedProcess))
                {
                    return preset;
                }
            }
        }
        return null;
    }
}
