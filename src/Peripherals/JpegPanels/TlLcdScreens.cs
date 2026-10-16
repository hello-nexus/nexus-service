using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Nexus.Service.Peripherals.FanLcd;
using Nexus.Service.Persistence;

namespace Nexus.Service.Peripherals.JpegPanels;

/// <summary>
/// The wired Uni Fan TL LCD screens as a <see cref="IFanLcdScreens"/> family. Every Nexus frame
/// goes out as SyncJPG, which the firmware shows only while frames keep coming: without one for
/// <see cref="KeepaliveMs"/> the last is re-sent, and <see cref="Release"/> stops that so the
/// glass falls back to its stored image. A screen Nexus never draws on gets no writes at all.
/// </summary>
public sealed class TlLcdScreens : IFanLcdScreens, IDisposable
{
    /// <summary>The firmware falls back to its stored image when frames stop; L-Connect never stops sending.</summary>
    public const int KeepaliveMs = 100;

    private readonly TlLcdPanels _panels;
    private readonly object _statesLock = new();
    private readonly Dictionary<string, ScreenState> _states = new(StringComparer.Ordinal);
    private readonly FanLcdPanelOwnership _panelOwnership = new();
    private Timer? _keepalive;
    private bool _disposed;

    private sealed class ScreenState
    {
        public readonly object Sync = new();
        public JpegPanelHub? Hub;
        public int WantedRotation;
        public int AppliedRotation = -1;
        public byte[]? Last;
        public long LastSentMs;
    }

    public TlLcdScreens(TlLcdPanels panels)
    {
        _panels = panels;
    }

    public string HandlerId => TlLcdPanels.GateHandlerId;

    public string LogTag => JpegPanelModel.TlLcd.HandlerId;

    public IReadOnlyList<string> AttachedSerials() => _panels.Attached.Select(s => s.Key).ToArray();

    public Dictionary<string, LianLiWirelessScreenSettings> SettingsOf(DevicesSettings devices) => devices.TlLcdScreens;

    public void PrepareFrames()
    {
    }

    /// <summary>L-Connect never sets this panel's backlight, so neither does Nexus.</summary>
    public void SyncBrightness(string serial, LianLiWirelessScreenSettings settings, ref int applied, ref bool lockSlept)
    {
    }

    /// <summary>Recorded only; it reaches the panel with the next frame, so a stock-firmware screen keeps its own rotation.</summary>
    public bool SetRotation(string serial, byte rotation)
    {
        var state = State(serial);
        lock (state.Sync)
        {
            state.WantedRotation = rotation;
        }
        return true;
    }

    public bool PushImage(string serial, byte[] jpeg)
    {
        if (_panels.ByKey(serial) is not { } screen)
        {
            return false;
        }
        var state = State(serial);
        lock (state.Sync)
        {
            state.Last = jpeg;
            StartKeepalive();
            return SendLocked(state, screen.Hub);
        }
    }

    public void Release(string serial)
    {
        var state = State(serial);
        lock (state.Sync)
        {
            state.Last = null;
        }
    }

    public void SetPanelOwned(string serial, bool owned) => _panelOwnership.Set(serial, owned);

    public bool PushPanelFrame(string serial, byte[] jpeg) => _panelOwnership.Push(serial, () => PushImage(serial, jpeg));

    public string PanelFamily => JpegPanelModel.TlLcd.HandlerId;

    public int PanelFps => JpegPanelModel.TlLcd.Fps;

    /// <summary>The wired Uni Fan TL screens play video at up to 30 frames a second, L-Connect's TL LCD video rate.</summary>
    public int MaxFps(string serial) => 30;

    public string PanelName(string serial) =>
        _panels.ByKey(serial) is { } screen
            ? TlLcdPanels.DisplayNameFor(screen.Handshake.Identity)
            : JpegPanelModel.TlLcd.Name;

    private ScreenState State(string serial)
    {
        lock (_statesLock)
        {
            if (!_states.TryGetValue(serial, out var state))
            {
                state = new ScreenState();
                _states[serial] = state;
            }
            return state;
        }
    }

    // A re-attached screen is a new hub whose panel has not been told the rotation yet.
    private static bool SendLocked(ScreenState state, JpegPanelHub hub)
    {
        if (!ReferenceEquals(state.Hub, hub))
        {
            state.Hub = hub;
            state.AppliedRotation = -1;
        }
        if (state.AppliedRotation != state.WantedRotation)
        {
            byte rotation = (byte)state.WantedRotation;
            if (hub.Exchange((device, report) => TlLcdHandshake.WriteDisplaySetting(device, report, rotation), false))
            {
                state.AppliedRotation = state.WantedRotation;
            }
        }
        bool sent = state.Last is { } frame && hub.SendFrame(frame);
        state.LastSentMs = Environment.TickCount64;
        return sent;
    }

    private void StartKeepalive()
    {
        lock (_statesLock)
        {
            if (_keepalive is null && !_disposed)
            {
                _keepalive = new Timer(_ => Keepalive(), null, KeepaliveMs, KeepaliveMs);
            }
        }
    }

    /// <summary>Re-sends each screen's last frame once it has gone quiet; skips a screen a send already holds.</summary>
    internal void Keepalive()
    {
        KeyValuePair<string, ScreenState>[] states;
        lock (_statesLock)
        {
            states = _states.ToArray();
        }
        long now = Environment.TickCount64;
        foreach (var (serial, state) in states)
        {
            if (!Monitor.TryEnter(state.Sync))
            {
                continue;
            }
            try
            {
                if (state.Last is null || now - state.LastSentMs < KeepaliveMs || _panels.ByKey(serial) is not { } screen)
                {
                    continue;
                }
                SendLocked(state, screen.Hub);
            }
            finally
            {
                Monitor.Exit(state.Sync);
            }
        }
    }

    public void Dispose()
    {
        lock (_statesLock)
        {
            _disposed = true;
            _keepalive?.Dispose();
            _keepalive = null;
        }
    }
}
