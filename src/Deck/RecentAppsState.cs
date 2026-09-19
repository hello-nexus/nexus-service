namespace Nexus.Service.Deck;

/// <summary>
/// The Recent Apps ring's currently focused process key, in-memory only (per
/// plan decision: RecentApps persists but focusedProcessKey does not).
/// RecentAppsService writes it on every focus change; StreamDeckConnectionWorker
/// reads it to render the focused-first, selected-key treatment without
/// depending on RecentAppsService itself (that dependency would run the other
/// way - RecentAppsService calls worker.RefreshView).
/// </summary>
public sealed class RecentAppsState
{
    private volatile string? _focusedProcessKey;

    public string? FocusedProcessKey => _focusedProcessKey;

    public void SetFocused(string? processKey) => _focusedProcessKey = processKey;
}
