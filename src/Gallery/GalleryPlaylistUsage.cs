using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using Nexus.Service.Models.Gallery;
using Nexus.Service.Models.Panel;
using Nexus.Service.Persistence;

namespace Nexus.Service.Gallery;

/// <summary>
/// Which gallery widgets play a playlist, across every place one can be
/// placed: the desktop dashboard and its saved presets, each panel device (including a Q-series'
/// remembered config for a gallery it is not showing right now) and the
/// pinned desktop widgets. Read-only; a deleted playlist's id is left in
/// those configs and resolves to the whole library.
/// </summary>
public static class GalleryPlaylistUsage
{
    private const string GalleryType = "gallery";
    private const string PlaylistKey = "playlistId";

    public static List<GalleryPlaylistUse> Find(NexusSettings s, string playlistId)
    {
        var uses = new List<GalleryPlaylistUse>();

        // The active preset's stored copy is stale; the live layout stands in for it.
        var dashboard = CountInLayout(s.Panel.DashboardLayout, playlistId)
            + (s.Panel.DashboardPresets?
                .Where(p => p.Id != s.Panel.DashboardActivePresetId)
                .Sum(p => CountInLayout(p.Layout, playlistId)) ?? 0);
        if (dashboard > 0)
        {
            uses.Add(new GalleryPlaylistUse { Surface = GalleryPlaylistUseSurfaces.Dashboard, Count = dashboard });
        }

        foreach (var device in s.PanelDevices.Values.OrderBy(d => d.DisplayName, StringComparer.OrdinalIgnoreCase))
        {
            var count = CountInLayout(device.Layout, playlistId);
            if (count == 0
                && device.Layout?.SingleWidgetConfigs is { } saved
                && saved.TryGetValue(GalleryType, out var config)
                && Plays(config, playlistId))
            {
                count = 1;
            }

            if (count > 0)
            {
                uses.Add(new GalleryPlaylistUse
                {
                    Surface = GalleryPlaylistUseSurfaces.Panel,
                    Name = device.DisplayName,
                    Count = count,
                });
            }
        }

        var desktop = s.Overlay.Layout.Count(w => w.Type == GalleryType && Plays(w.Config, playlistId));
        if (desktop > 0)
        {
            uses.Add(new GalleryPlaylistUse { Surface = GalleryPlaylistUseSurfaces.Desktop, Count = desktop });
        }

        return uses;
    }

    private static int CountInLayout(PanelLayoutDto? layout, string playlistId) =>
        layout?.Pages.Sum(p => p.Widgets.Count(w => w.Type == GalleryType && Plays(w.Config, playlistId))) ?? 0;

    private static bool Plays(Dictionary<string, JsonElement>? config, string playlistId) =>
        config is not null
        && config.TryGetValue(PlaylistKey, out var value)
        && value.ValueKind == JsonValueKind.String
        && value.GetString() == playlistId;
}
