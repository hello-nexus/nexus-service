using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Nexus.Service.Hotkeys;

/// <summary>One combo to register with the OS under a binding id.</summary>
public readonly record struct HotkeyRegistration(string Id, HotkeyCombo Combo);

/// <summary>
/// The OS-level registration surface: macOS Carbon in-process, the Windows
/// user helper over the pipe, nothing on Linux. Registration only; no key
/// capture of any kind.
/// </summary>
public interface IHotkeyRegistrar
{
    /// <summary>macos | windows | linux.</summary>
    string Platform { get; }

    /// <summary>False when this platform cannot register global hotkeys at all.</summary>
    bool Supported { get; }

    /// <summary>
    /// Makes exactly <paramref name="set"/> the registered set (an empty set unregisters
    /// everything) and reports per id whether the OS accepted it. Ids absent from the
    /// result were not attempted.
    /// </summary>
    Task<IReadOnlyDictionary<string, bool>> ApplyAsync(IReadOnlyList<HotkeyRegistration> set, CancellationToken ct);

    /// <summary>A registered combo was pressed; the argument is the binding id. Raised on any thread.</summary>
    event Action<string>? Fired;

    /// <summary>The registrar lost its registrations and wants the set pushed again (the Windows helper reconnected).</summary>
    event Action? NeedsReapply;
}

/// <summary>Platforms without global hotkey support (Linux): everything reports unsupported.</summary>
public sealed class UnsupportedHotkeyRegistrar : IHotkeyRegistrar
{
    public string Platform => "linux";
    public bool Supported => false;

#pragma warning disable CS0067 // never raised: nothing registers here
    public event Action<string>? Fired;
    public event Action? NeedsReapply;
#pragma warning restore CS0067

    public Task<IReadOnlyDictionary<string, bool>> ApplyAsync(IReadOnlyList<HotkeyRegistration> set, CancellationToken ct)
        => Task.FromResult<IReadOnlyDictionary<string, bool>>(new Dictionary<string, bool>());
}
