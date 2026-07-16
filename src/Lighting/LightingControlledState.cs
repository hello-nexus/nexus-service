using System.Collections.Generic;
using Nexus.Service.Persistence;

namespace Nexus.Service.Lighting;

/// <summary>
/// Persists the "controlled" flag for lighting device ids. No provider owns this
/// action - uncontrolled is pure persisted state every frame writer consults, so
/// the mutation lives here rather than on <see cref="Devices.ILightingDeviceProvider"/>.
/// </summary>
public static class LightingControlledState
{
    /// <summary>
    /// Write id's controlled state, replacing the list reference rather than
    /// mutating in place so the frame-rate writers reading it lock-free
    /// never observe a torn state.
    /// </summary>
    public static void SetControlled(string id, bool controlled, IConfigStore store)
    {
        store.Update(s =>
        {
            var current = s.Devices.UncontrolledLightingDevices;
            if (controlled)
            {
                if (!current.Contains(id))
                {
                    return;
                }
                var next = new List<string>(current.Count);
                foreach (var x in current)
                {
                    if (x != id)
                    {
                        next.Add(x);
                    }
                }
                s.Devices.UncontrolledLightingDevices = next;
            }
            else
            {
                if (current.Contains(id))
                {
                    return;
                }
                var next = new List<string>(current.Count + 1);
                next.AddRange(current);
                next.Add(id);
                s.Devices.UncontrolledLightingDevices = next;
            }
        });
    }
}
