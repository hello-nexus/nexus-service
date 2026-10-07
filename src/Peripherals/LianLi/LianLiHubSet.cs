using System;
using System.Collections.Generic;
using System.Globalization;
using Nexus.Service.Persistence;

namespace Nexus.Service.Peripherals.LianLi;

/// <summary>
/// Every Uni hub slot. The first keeps the "lianli" id and the original
/// settings fields; each further hub is "lianli2", "lianli3", ... so its
/// device, zone and channel ids never collide with another hub's.
/// </summary>
public sealed class LianLiHubSet
{
    public const string PrimaryId = "lianli";
    public const int Capacity = 4;

    public LianLiHubSet()
    {
        var hubs = new LianLiHub[Capacity];
        for (var i = 0; i < Capacity; i++)
        {
            hubs[i] = new LianLiHub(IdFor(i));
        }
        Hubs = hubs;
    }

    public IReadOnlyList<LianLiHub> Hubs { get; }

    public LianLiHub Primary => Hubs[0];

    /// <summary>Any slot's hub is attached or on the bus.</summary>
    public bool AnyPresent
    {
        get
        {
            foreach (var hub in Hubs)
            {
                if (hub.IsConnected || hub.Present) return true;
            }
            return false;
        }
    }

    public bool AnyConnected
    {
        get
        {
            foreach (var hub in Hubs)
            {
                if (hub.IsConnected) return true;
            }
            return false;
        }
    }

    public static string IdFor(int slot) => slot == 0 ? PrimaryId : PrimaryId + (slot + 1).ToString(CultureInfo.InvariantCulture);

    /// <summary>Slot of the hub a hub, device, zone or channel id belongs to ("lianli2:port0" is slot 1); -1 for any other id.</summary>
    public static int SlotOf(string? id)
    {
        if (string.IsNullOrEmpty(id) || !id.StartsWith(PrimaryId, StringComparison.Ordinal)) return -1;
        var rest = id.AsSpan(PrimaryId.Length);
        var colon = rest.IndexOf(':');
        var digits = colon < 0 ? rest : rest[..colon];
        if (digits.Length == 0) return 0;
        if (!int.TryParse(digits, NumberStyles.None, CultureInfo.InvariantCulture, out var n) || n < 2 || n > Capacity) return -1;
        return digits.ToString() == n.ToString(CultureInfo.InvariantCulture) ? n - 1 : -1;
    }

    public static bool OwnsId(string? id) => SlotOf(id) >= 0;

    /// <summary>The hub an id belongs to (see <see cref="SlotOf"/>), or null.</summary>
    public LianLiHub? Owner(string? id)
    {
        var slot = SlotOf(id);
        return slot < 0 ? null : Hubs[slot];
    }

    public void OnSystemResumed()
    {
        foreach (var hub in Hubs)
        {
            hub.OnSystemResumed();
        }
    }

    /// <summary>A hub's port fan counts; an extra hub never configured reads the defaults.</summary>
    public static LianLiSettings FansOf(DevicesSettings devices, string hubId) =>
        hubId == PrimaryId ? devices.LianLi
        : devices.LianLiExtraHubs.TryGetValue(hubId, out var hub) ? hub.Fans
        : new LianLiSettings();

    /// <summary>A hub's lighting settings; an extra hub never configured reads the defaults.</summary>
    public static LianLiLightingSettings LightingOf(DevicesSettings devices, string hubId) =>
        hubId == PrimaryId ? devices.LianLiLighting
        : devices.LianLiExtraHubs.TryGetValue(hubId, out var hub) ? hub.Lighting
        : new LianLiLightingSettings();

    /// <summary>
    /// A hub's settings for a store update, created on first use. Readers walk
    /// the map without the store lock, so it is replaced, never added to.
    /// </summary>
    public static (LianLiSettings Fans, LianLiLightingSettings Lighting) Editable(DevicesSettings devices, string hubId)
    {
        if (hubId == PrimaryId) return (devices.LianLi, devices.LianLiLighting);
        if (!devices.LianLiExtraHubs.TryGetValue(hubId, out var hub))
        {
            hub = new LianLiHubSettings();
            devices.LianLiExtraHubs = new Dictionary<string, LianLiHubSettings>(devices.LianLiExtraHubs) { [hubId] = hub };
        }
        return (hub.Fans, hub.Lighting);
    }
}
