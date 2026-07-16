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
