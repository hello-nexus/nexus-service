using System.Collections.Generic;
using Nexus.Service.Persistence;

namespace Nexus.Service.Models.Profiles;

public class ListProfilesResponse : ApiResponse
{
    public List<ProfileEntry> Profiles { get; set; } = new();
    public string ActiveId { get; set; } = "";
}

public class ProfileResponse : ApiResponse
{
    public ProfileEntry? Profile { get; set; }

    /// <summary>What an import did with a PC setup in the bundle: "applied", "applied_unknown_machine" (the file names no machine) or "skipped_other_machine". Null when the bundle carried none.</summary>
    public string? PcSetup { get; set; }
}

public class SwitchProfileResponse : ApiResponse
{
    public string Switched { get; set; } = "";
    public Preferences? Prefs { get; set; }
}

public class CreateProfileBody
{
    public string Name { get; set; } = "";
}

public class RenameProfileBody
{
    public string Name { get; set; } = "";
}

public class ImportProfileBody
{
    public string Name { get; set; } = "";
    public NexusSettings? Data { get; set; }
}

public sealed class ProfileExport
{
    public string? Name { get; set; }
    public NexusSettings? Settings { get; set; }

    /// <summary>The profile's app data as <c>{ appId: { key: data } }</c>. Absent in a legacy export and in the settings payload of a cloud backup.</summary>
    public Dictionary<string, Dictionary<string, System.Text.Json.JsonElement>>? AppData { get; set; }

    /// <summary>The machine a profile FILE was exported on; lets an import tell its own machine's file from another's. Absent in a cloud payload (the row carries the install id there) and in older files.</summary>
    public string? InstallId { get; set; }
}

/// <summary>Response of <c>POST /profiles/import/inspect</c>; nothing is written.</summary>
public sealed class ProfileInspectResponse : ApiResponse
{
    public string Name { get; set; } = "";
    public List<string> AppIds { get; set; } = new();

    /// <summary>"json" for a bundle or a legacy bare JSON file, "archive" for a legacy zip.</summary>
    public string Format { get; set; } = "json";
}

public class SharingResponse : ApiResponse
{
    public string? PrimaryProfileId { get; set; }
    public List<string> SharedCategories { get; set; } = new();
    public List<string> AllCategories { get; set; } = new();
    /// <summary>Preset count per category id for the active profile (lighting layout presets, Stream Deck presets across every deck). Categories with no preset concept report 0.</summary>
    public Dictionary<string, int> Counts { get; set; } = new();
}

public class SetPrimaryBody
{
    public string ProfileId { get; set; } = "";
}

public class SetCategorySharedBody
{
    public string Category { get; set; } = "";
    public bool Shared { get; set; }
}
