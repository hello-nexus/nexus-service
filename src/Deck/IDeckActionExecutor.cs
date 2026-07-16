using System.Threading;
using System.Threading.Tasks;

namespace Nexus.Service.Deck;

/// <summary>
/// Headless dispatcher for a <see cref="DeckAction"/> - the service-side
/// port of nexus-web's <c>deckExecutor.ts</c>. Split from the concrete
/// <see cref="DeckActionExecutor"/> so callers (the connection worker,
/// routes) depend on the interface and tests can substitute a spy without
/// constructing every provider the real implementation touches.
/// </summary>
public interface IDeckActionExecutor
{
    /// <summary>
    /// Runs one action to completion, logging exactly one outcome line.
    /// Never throws. <paramref name="latchKey"/> identifies the physical key
    /// (deck + folder path + slot index) for toggle state that falls back to
    /// an in-memory flip when no live state applies.
    /// </summary>
    Task ExecuteAsync(DeckAction? action, string serial, int keyIndex, string latchKey, CancellationToken ct);

    /// <summary>
    /// Resolves whether a toggle is currently on: a live query (mute,
    /// lightingPower) when the state kind supports one, else the latch
    /// recorded by the last dispatch through <paramref name="latchKey"/>.
    /// </summary>
    bool IsToggleOn(DeckToggleState? state, string latchKey);

    /// <summary>
    /// Opens or focuses the Nexus desktop app (SystemActions.OpenDashboard),
    /// so a blank-key hold-to-edit can bring the editor to the foreground.
    /// May block on an interactive-session launch; callers dispatch it off
    /// their own thread.
    /// </summary>
    void OpenApp();
}
