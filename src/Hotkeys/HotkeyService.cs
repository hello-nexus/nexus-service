using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using Nexus.Service.Deck;
using Nexus.Service.Persistence;
using Nexus.Service.Platform;
using Nexus.Service.Serialization;
using Nexus.Service.Sockets;

namespace Nexus.Service.Hotkeys;

/// <summary>
/// Owns the global hotkey bindings: seeds the defaults once, keeps the OS
/// registrations in step with the stored bindings (enabled, parseable, not
/// orphaned, not duplicated), runs a binding's target when its combo fires, and
/// broadcasts the status payload on the "hotkeys" topic.
/// </summary>
public sealed class HotkeyService : IHostedService, IDisposable
{
    public const int MaxBindings = 100;
    private const int MaxIdLength = 64;
    internal static readonly TimeSpan DefaultSuspendTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan StopGateTimeout = TimeSpan.FromSeconds(1);

    private readonly IConfigStore _store;
    private readonly IHotkeyRegistrar _registrar;
    private readonly IDeckActionExecutor _executor;
    private readonly MultiplexHub _hub;
    private readonly bool _macos;
    private readonly SemaphoreSlim _reconcileGate = new(1, 1);
    private readonly object _stateLock = new();

    // Registration results of the last apply, by binding id. Replaced whole.
    private IReadOnlyDictionary<string, bool> _results = new Dictionary<string, bool>();
    private string? _appliedKey;
    private byte[]? _lastEnvelope;
    private volatile bool _suspended;
    private Timer? _resumeTimer;
    private int _reconcileQueued;
    private bool _disposed;
    private volatile bool _stopped;
    private int _suspendGeneration;

    /// <summary>Test seam: how long a suspend lasts before it resumes on its own.</summary>
    internal TimeSpan SuspendTimeout { get; set; } = DefaultSuspendTimeout;

    /// <summary>Releases the combo's modifier keys before a keystroke action is injected; null skips it.</summary>
    internal Func<HotkeyCombo, Task>? ReleaseModifiers { get; set; }

    public HotkeyService(IConfigStore store, IHotkeyRegistrar registrar, IDeckActionExecutor executor, MultiplexHub hub, Nexus.Service.Actions.SystemActions system)
        : this(store, registrar, executor, hub, OperatingSystem.IsMacOS())
    {
        ReleaseModifiers = combo => ReleaseModifiersAsync(system, combo);
    }

    internal HotkeyService(IConfigStore store, IHotkeyRegistrar registrar, IDeckActionExecutor executor, MultiplexHub hub, bool macos)
    {
        _store = store;
        _registrar = registrar;
        _executor = executor;
        _hub = hub;
        _macos = macos;
        _hub.RegisterSnapshotProvider(PanelTopics.Hotkeys, () => BuildEnvelope(BuildResponse()));
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        SeedDefaults();
        _registrar.Fired += OnFired;
        _registrar.NeedsReapply += OnNeedsReapply;
        _store.OnChanged += OnStoreChanged;
        return ReconcileAsync(force: true);
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        _store.OnChanged -= OnStoreChanged;
        _registrar.Fired -= OnFired;
        _registrar.NeedsReapply -= OnNeedsReapply;
        lock (_stateLock)
        {
            _suspendGeneration++;
            _resumeTimer?.Dispose();
            _resumeTimer = null;
        }
        // Holding the gate makes an in-flight reconcile finish first, but a registrar stuck on a main queue that no longer runs must not hold up shutdown.
        var gated = await _reconcileGate.WaitAsync(StopGateTimeout).ConfigureAwait(false);
        _stopped = true;
        _suspended = false;
        if (!gated)
        {
            return;
        }
        try
        {
            if (_registrar.Supported && _registrar.ReleaseOnStop)
            {
                await SafeApplyAsync(Array.Empty<HotkeyRegistration>()).ConfigureAwait(false);
            }
        }
        finally
        {
            _reconcileGate.Release();
        }
    }

    public void Dispose()
    {
        lock (_stateLock)
        {
            _disposed = true;
            _resumeTimer?.Dispose();
            _resumeTimer = null;
        }
    }

    // ---- Queries -----------------------------------------------------------

    public HotkeysResponse BuildResponse()
    {
        var results = _results;
        var views = new List<HotkeyBindingView>();
        _store.Read(settings =>
        {
            var bindings = settings.Hotkeys.Bindings;
            var analysis = Analyze(bindings, settings.StreamDeck);
            foreach (var b in bindings)
            {
                views.Add(new HotkeyBindingView
                {
                    Id = b.Id,
                    Combo = b.Combo,
                    Enabled = b.Enabled,
                    Label = b.Label,
                    Target = b.Target,
                    Status = StatusOf(b, analysis, results),
                });
            }
        });
        return new HotkeysResponse
        {
            Supported = _registrar.Supported,
            Platform = _registrar.Platform,
            Suspended = _suspended,
            Bindings = views,
        };
    }

    // ---- Mutations ---------------------------------------------------------

    /// <summary>The error code and message for why <paramref name="binding"/> cannot be stored, or null when it is valid. Normalizes Combo to canonical form.</summary>
    public static (string Code, string Msg)? Validate(HotkeyBinding binding)
    {
        if (!ValidId(binding.Id))
        {
            return (HotkeyErrorCodes.InvalidId, "invalid hotkey id");
        }
        if (string.IsNullOrWhiteSpace(binding.Combo))
        {
            binding.Combo = null;
        }
        else
        {
            var canonical = HotkeyCombo.Canonicalize(binding.Combo);
            if (canonical is null)
            {
                return (HotkeyErrorCodes.InvalidCombo, "invalid combo");
            }
            binding.Combo = canonical;
        }
        if (binding.Target is not { } target)
        {
            return (HotkeyErrorCodes.InvalidTarget, "target is required");
        }
        switch (target.Kind)
        {
            case HotkeyTarget.KindAction:
                if (target.Action is null || string.IsNullOrEmpty(target.Action.Type))
                {
                    return (HotkeyErrorCodes.InvalidTarget, "action target needs an action");
                }
                break;
            case HotkeyTarget.KindDeck:
                if (string.IsNullOrEmpty(target.PresetId) || !ValidId(target.Tag))
                {
                    return (HotkeyErrorCodes.InvalidTarget, "deck target needs presetId and a valid tag");
                }
                break;
            default:
                return (HotkeyErrorCodes.InvalidTarget, "unknown target kind");
        }
        return null;
    }

    private static bool ValidId(string? id)
        => !string.IsNullOrEmpty(id) && id.Length <= MaxIdLength && id.All(IsIdChar);

    /// <summary>Stores the (already validated) binding, replacing one with the same id. False when the binding cap is hit.</summary>
    public async Task<bool> UpsertAsync(HotkeyBinding binding)
    {
        var stored = true;
        _store.Update(s =>
        {
            var list = s.Hotkeys.Bindings.ToList();
            var index = list.FindIndex(b => b.Id == binding.Id);
            if (index >= 0)
            {
                list[index] = binding;
            }
            else if (list.Count >= MaxBindings)
            {
                stored = false;
                return;
            }
            else
            {
                list.Add(binding);
            }
            s.Hotkeys.Bindings = list;
        });
        await ReconcileAsync().ConfigureAwait(false);
        return stored;
    }

    public async Task<bool> DeleteAsync(string id)
    {
        var removed = false;
        _store.Update(s =>
        {
            var list = s.Hotkeys.Bindings.ToList();
            removed = list.RemoveAll(b => b.Id == id) > 0;
            if (removed)
            {
                s.Hotkeys.Bindings = list;
            }
        });
        await ReconcileAsync().ConfigureAwait(false);
        return removed;
    }

    /// <summary>Re-creates or overwrites the built-in bindings with their defaults; custom bindings are untouched.</summary>
    public async Task ResetDefaultsAsync()
    {
        var defaults = HotkeyDefaults.Build(_macos);
        _store.Update(s =>
        {
            var list = s.Hotkeys.Bindings.ToList();
            foreach (var d in defaults)
            {
                var index = list.FindIndex(b => b.Id == d.Id);
                if (index >= 0)
                {
                    list[index] = d;
                }
                else
                {
                    list.Add(d);
                }
            }
            s.Hotkeys.Bindings = list;
            s.Hotkeys.DefaultsSeeded = true;
        });
        await ReconcileAsync().ConfigureAwait(false);
    }

    /// <summary>Unregisters every hotkey (true) or registers them again (false). A suspend resumes on its own after the timeout.</summary>
    public async Task SetSuspendedAsync(bool suspended)
    {
        lock (_stateLock)
        {
            if (_disposed || _stopped)
            {
                return;
            }
            var generation = ++_suspendGeneration;
            _suspended = suspended;
            _resumeTimer?.Dispose();
            _resumeTimer = suspended
                ? new Timer(_ => _ = ResumeAsync(generation), null, SuspendTimeout, Timeout.InfiniteTimeSpan)
                : null;
        }
        await ReconcileAsync().ConfigureAwait(false);
    }

    // A timer callback already in flight when a newer suspend or resume lands must not undo it.
    internal async Task ResumeAsync(int generation)
    {
        lock (_stateLock)
        {
            if (_disposed || _stopped || generation != _suspendGeneration)
            {
                return;
            }
            _suspendGeneration++;
            _suspended = false;
            _resumeTimer?.Dispose();
            _resumeTimer = null;
        }
        try
        {
            await ReconcileAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            ServiceLog.Warn($"[hotkeys] resume failed: {ex.Message}");
        }
    }

    // ---- Reconciliation ----------------------------------------------------

    /// <summary>
    /// Brings the OS registrations in step with the stored bindings and
    /// broadcasts the payload when anything a client sees changed. Serialized.
    /// </summary>
    public async Task ReconcileAsync(bool force = false)
    {
        await _reconcileGate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_stopped)
            {
                return;
            }
            var suspended = _suspended;
            List<HotkeyRegistration> wanted = new();
            if (!suspended && _registrar.Supported)
            {
                _store.Read(settings => wanted = WantedRegistrations(settings.Hotkeys.Bindings, settings.StreamDeck));
            }
            var key = string.Join('|', wanted.Select(r => r.Id + "=" + r.Combo));
            if (force || key != _appliedKey)
            {
                if (_registrar.Supported)
                {
                    var applied = await SafeApplyAsync(wanted).ConfigureAwait(false);
                    // While suspended nothing is registered; keep the last real results so statuses stay what they would be.
                    if (!suspended)
                    {
                        _results = applied;
                    }
                }
                _appliedKey = key;
            }
            Broadcast();
        }
        finally
        {
            _reconcileGate.Release();
        }
    }

    private async Task<IReadOnlyDictionary<string, bool>> SafeApplyAsync(IReadOnlyList<HotkeyRegistration> set)
    {
        try
        {
            return await _registrar.ApplyAsync(set, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            ServiceLog.Warn($"[hotkeys] apply failed: {ex.Message}");
            return set.ToDictionary(r => r.Id, _ => false);
        }
    }

    private void OnStoreChanged()
    {
        // One queued pass at a time; the flag clears before the pass reads settings, so a change during the pass queues the next.
        if (Interlocked.Exchange(ref _reconcileQueued, 1) == 1)
        {
            return;
        }
        _ = Task.Run(async () =>
        {
            Interlocked.Exchange(ref _reconcileQueued, 0);
            try
            {
                await ReconcileAsync().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                ServiceLog.Warn($"[hotkeys] reconcile failed: {ex.Message}");
            }
        });
    }

    private void OnNeedsReapply()
    {
        _ = Task.Run(async () =>
        {
            try
            {
                await ReconcileAsync(force: true).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                ServiceLog.Warn($"[hotkeys] reapply failed: {ex.Message}");
            }
        });
    }

    internal void SeedDefaults()
    {
        _store.Update(s =>
        {
            if (s.Hotkeys.DefaultsSeeded)
            {
                return;
            }
            var list = s.Hotkeys.Bindings.ToList();
            foreach (var d in HotkeyDefaults.Build(_macos))
            {
                if (!list.Any(b => b.Id == d.Id))
                {
                    list.Add(d);
                }
            }
            s.Hotkeys.Bindings = list;
            s.Hotkeys.DefaultsSeeded = true;
        });
    }

    private void Broadcast()
    {
        var envelope = BuildEnvelope(BuildResponse());
        var previous = _lastEnvelope;
        if (previous is not null && previous.AsSpan().SequenceEqual(envelope.Span))
        {
            return;
        }
        _lastEnvelope = envelope.ToArray();
        if (_hub.TopicHasSubscribers(PanelTopics.Hotkeys))
        {
            _ = _hub.BroadcastTopicAsync(PanelTopics.Hotkeys, envelope);
        }
    }

    private static ReadOnlyMemory<byte> BuildEnvelope(HotkeysResponse response)
        => WsEnvelope.Build(PanelTopics.Hotkeys, response, AppJsonContext.Default.HotkeysResponse);

    // ---- Eligibility and status --------------------------------------------

    // One pass over the bindings per reconcile or response: each deck target is resolved once.
    private sealed class Analysis
    {
        public readonly List<(HotkeyBinding Binding, HotkeyCombo Combo)> Eligible = new();
        public readonly Dictionary<string, int> ComboCounts = new(StringComparer.Ordinal);
        public readonly HashSet<string> Orphaned = new(StringComparer.Ordinal);
    }

    private static Analysis Analyze(IReadOnlyList<HotkeyBinding> bindings, StreamDeckSettings deck)
    {
        var analysis = new Analysis();
        foreach (var b in bindings)
        {
            if (!b.Enabled || HotkeyCombo.TryParse(b.Combo) is not { } combo)
            {
                continue;
            }
            if (b.Target.Kind == HotkeyTarget.KindDeck && HotkeySlotResolver.Resolve(b.Target, deck) is null)
            {
                analysis.Orphaned.Add(b.Id);
                continue;
            }
            analysis.Eligible.Add((b, combo));
            var text = combo.ToString();
            analysis.ComboCounts[text] = analysis.ComboCounts.GetValueOrDefault(text) + 1;
        }
        return analysis;
    }

    private static List<HotkeyRegistration> WantedRegistrations(IReadOnlyList<HotkeyBinding> bindings, StreamDeckSettings deck)
    {
        var analysis = Analyze(bindings, deck);
        var wanted = new List<HotkeyRegistration>();
        foreach (var (b, combo) in analysis.Eligible)
        {
            if (analysis.ComboCounts[combo.ToString()] == 1)
            {
                wanted.Add(new HotkeyRegistration(b.Id, combo));
            }
        }
        return wanted;
    }

    private string StatusOf(HotkeyBinding b, Analysis analysis, IReadOnlyDictionary<string, bool> results)
    {
        if (!_registrar.Supported)
        {
            return HotkeyStatus.Unsupported;
        }
        if (!b.Enabled)
        {
            return HotkeyStatus.Disabled;
        }
        if (HotkeyCombo.TryParse(b.Combo) is not { } combo)
        {
            return HotkeyStatus.Unbound;
        }
        if (analysis.Orphaned.Contains(b.Id))
        {
            return HotkeyStatus.Orphaned;
        }
        if (analysis.ComboCounts.GetValueOrDefault(combo.ToString()) > 1)
        {
            return HotkeyStatus.Conflict;
        }
        return results.TryGetValue(b.Id, out var ok) && !ok ? HotkeyStatus.Conflict : HotkeyStatus.Active;
    }

    // ---- Firing ------------------------------------------------------------

    private void OnFired(string id, string combo)
    {
        if (_suspended)
        {
            return;
        }
        _ = Task.Run(() => FireAsync(id, combo));
    }

    /// <summary>Runs the binding named by <paramref name="id"/>; a press whose combo is not the binding's current one is stale and ignored.</summary>
    internal async Task FireAsync(string id, string? firedCombo = null)
    {
        try
        {
            HotkeyBinding? binding = null;
            DeckAction? action = null;
            _store.Read(settings =>
            {
                binding = settings.Hotkeys.Bindings.FirstOrDefault(b => b.Id == id);
                if (binding is null || !binding.Enabled)
                {
                    binding = null;
                    return;
                }
                action = binding.Target.Kind == HotkeyTarget.KindDeck
                    ? HotkeySlotResolver.Resolve(binding.Target, settings.StreamDeck)
                    : binding.Target.Action;
            });
            if (binding is null)
            {
                return;
            }
            if (firedCombo is not null && HotkeyCombo.Canonicalize(firedCombo) != HotkeyCombo.Canonicalize(binding.Combo))
            {
                ServiceLog.Info($"[hotkeys] ignored stale press id={id}");
                return;
            }
            if (action is null)
            {
                ServiceLog.Info($"[hotkeys] fired id={id} target no longer resolves");
                return;
            }
            ServiceLog.Info($"[hotkeys] fired id={id} type={action.Type}");
            // The trigger's modifiers are still physically down; release them so an injected chord does not merge with them.
            if (action.Type is "hotkey" or "hotkeySwitch" && ReleaseModifiers is { } release && HotkeyCombo.TryParse(binding.Combo) is { } combo)
            {
                await release(combo).ConfigureAwait(false);
            }
            await _executor.ExecuteAsync(action, serial: "", keyIndex: -1, latchKey: "hotkey:" + id, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            ServiceLog.Warn($"[hotkeys] fire failed id={id}: {ex.Message}");
        }
    }

    private static Task ReleaseModifiersAsync(Nexus.Service.Actions.SystemActions system, HotkeyCombo combo)
        => system.SendKeysAsync(new Nexus.Service.Models.Activity.SendKeysBody
        {
            Strokes =
            {
                new Nexus.Service.Models.Peripherals.Keeb.MacroStroke
                {
                    Type = Nexus.Service.Models.Peripherals.Keeb.MacroStroke.ReleaseModifiersType,
                    Ctrl = combo.Ctrl, Alt = combo.Alt, Shift = combo.Shift, Meta = combo.Meta,
                },
            },
        });

    // ---- Validation helpers ------------------------------------------------

    private static bool IsIdChar(char c) => char.IsAsciiLetterOrDigit(c) || c is ':' or '_' or '.' or '-';
}
