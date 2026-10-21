using System;
using Nexus.Service.Persistence;

namespace Nexus.Service.Peripherals.BulkPanels;

/// <summary>The Nexus panel gate: read once into <see cref="GlacialLcdLink.NexusPanelEnabled"/>; every other check asks the link.</summary>
public static class GlacialLcdNexusPanelGate
{
    public static bool Read(IConfigStore store)
    {
        bool on = false;
        store.Read(s => on = s.Devices.GlacialLcdNexusPanel);
        return on;
    }

    /// <summary>True while the display keeps its own modes: the gate is off, or the user has not picked the Nexus panel mode.</summary>
    public static Func<bool> Withheld(GlacialLcdLink link, IConfigStore store) => () =>
    {
        if (!link.NexusPanelEnabled)
        {
            return true;
        }
        bool chosen = false;
        store.Read(s => chosen = GlacialLcdSettings.StreamChosen(s.Devices.GlacialLcd));
        return !chosen;
    };
}
