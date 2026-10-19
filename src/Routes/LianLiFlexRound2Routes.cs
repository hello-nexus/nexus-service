using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.AspNetCore.Http;
using Nexus.Service.Cooling;
using Nexus.Service.Lighting;
using Nexus.Service.Models;
using Nexus.Service.Peripherals.LianLiFlex;
using Nexus.Service.Persistence;
using Nexus.Service.Serialization;

namespace Nexus.Service.Routes;

public sealed class LianLiFlexSyncMemberDto
{
    public string Mac { get; set; } = "";
    public string Family { get; set; } = "";
    public string Label { get; set; } = "";
    public int SortIndex { get; set; }
    public int SliceDirection { get; set; }
}

public sealed class LianLiFlexSyncMemberPatch
{
    public string Mac { get; set; } = "";
    public int SliceDirection { get; set; }
}

public sealed class LianLiFlexSyncDto
{
    public bool Enabled { get; set; }
    /// <summary>The unit whose look drives the merged effect; empty when none merges.</summary>
    public string LeaderMac { get; set; } = "";
    public string Mode { get; set; } = "";
    public int Speed { get; set; }
    public int Direction { get; set; }
    public int Brightness { get; set; }
    public string[] Colors { get; set; } = Array.Empty<string>();
    public LianLiFlexModeDto[] Modes { get; set; } = Array.Empty<LianLiFlexModeDto>();
    public LianLiFlexSyncMemberDto[] Members { get; set; } = Array.Empty<LianLiFlexSyncMemberDto>();
}

public sealed class LianLiFlexSyncPatch
{
    public bool? Enabled { get; set; }
    public string? Mode { get; set; }
    public int? Speed { get; set; }
    public int? Direction { get; set; }
    public int? Brightness { get; set; }
    public string[]? Colors { get; set; }
    public LianLiFlexSyncMemberPatch[]? Members { get; set; }
}

public static partial class LianLiFlexRoutes
{
    private static void MapRound2Endpoints(WebApplication app)
    {
        app.MapGet("/devices/lianli-flex/sync", (FlexGroupLighting group) =>
            Results.Json(SyncDto(group), AppJsonContext.Default.LianLiFlexSyncDto));

        app.MapPut("/devices/lianli-flex/sync", (
            LianLiFlexSyncPatch body, FlexGroupLighting group, Nexus.Service.Sockets.MultiplexHub mux) =>
        {
            var error = group.Apply(new FlexSyncPatch
            {
                Enabled = body.Enabled,
                Mode = body.Mode,
                Speed = body.Speed,
                Direction = body.Direction,
                Brightness = body.Brightness,
                Colors = body.Colors,
                Members = body.Members?.Select(m => (m.Mac, m.SliceDirection)).ToList(),
            });
            if (error is not null)
            {
                return Results.BadRequest(ApiResponse.Fail(error));
            }
            Nexus.Service.Sockets.PanelTopics.BroadcastLighting(mux);
            return Results.Json(ApiResponse.Ok(), AppJsonContext.Default.ApiResponse);
        });
    }

    private static LianLiFlexModeDto SyncMode(LianLiFlexModeDto m)
    {
        var count = FlexSwatchCounts.OfSync(m.Key);
        return new LianLiFlexModeDto
        {
            Key = m.Key,
            HasSpeed = m.HasSpeed,
            HasDirection = m.HasDirection,
            ColorsMin = 0,
            ColorsMax = count,
            Mergeable = true,
            DefaultColors = FitDefaults(m.DefaultColors, count),
        };
    }

    internal static LianLiFlexSyncDto SyncDto(FlexGroupLighting group)
    {
        var leader = group.Leader();
        var members = group.Members();
        var family = members.Count > 0 ? members[0].Family : FlexFamily.TlFlex;
        return new LianLiFlexSyncDto
        {
            Enabled = group.Enabled,
            Mode = FlexGroupLighting.SyncModeOf(leader.Mode),
            LeaderMac = group.LeaderMac(),
            Speed = leader.Speed,
            Direction = leader.Direction,
            Brightness = leader.Brightness,
            Colors = leader.Colors.ToArray(),
            Modes = ModesOf(family).Where(m => m.Mergeable).Select(m => SyncMode(m)).ToArray(),
            Members = members.Select(m => new LianLiFlexSyncMemberDto
            {
                Mac = m.Mac,
                Family = m.Family == FlexFamily.SlInfFlex ? "slInfFlex" : "tlFlex",
                Label = FlexLightingDeviceProvider.ModelName(m.Family),
                SortIndex = m.SortIndex,
                SliceDirection = m.SliceDirection,
            }).ToArray(),
        };
    }
}
