using System.Collections.Generic;
using Nexus.Service.Deck;

namespace Nexus.Service.Models.Peripherals.StreamDeck;

/// <summary>One entry of GET /streamdeck/elgato/profiles.</summary>
public sealed class ElgatoProfileSummaryDto
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string Model { get; set; } = "";
    public string ModelLabel { get; set; } = "";
    public int PageCount { get; set; }
    public int KeyCount { get; set; }
}

/// <summary>GET /streamdeck/elgato/profiles response.</summary>
public sealed class ElgatoProfilesResponse
{
    /// <summary>"ok" | "notFound" | "unsupportedVersion".</summary>
    public string Status { get; set; } = "";
    public List<ElgatoProfileSummaryDto> Profiles { get; set; } = new();
}

/// <summary>One key the translator could not (or only partly) map, from POST .../import.</summary>
public sealed class ElgatoUnmappedEntry
{
    /// <summary>1-based top-level page index; nested-folder entries report their top-level ancestor's page.</summary>
    public int Page { get; set; }
    /// <summary>"col,row" within whichever grid the key lives in (the top page, or a folder's own grid); empty for a whole-page note.</summary>
    public string Position { get; set; } = "";
    public string Name { get; set; } = "";
    /// <summary>hotkey | open | website | text | media | plugin | unsupported | multiStep | encoder | pageLimit | hotkeyExtraSlots | textEnterIgnored | monitoringSensor | audioPath.</summary>
    public string Reason { get; set; } = "";
    public string? Detail { get; set; }
}

/// <summary>POST /streamdeck/elgato/profiles/{id}/import report summary.</summary>
public sealed class ElgatoImportReport
{
    /// <summary>Every translatable-relevant key across the profile (top pages and folders), excluding tutorial tiles, backtoparent, and empty cells.</summary>
    public int TotalKeys { get; set; }
    /// <summary>TotalKeys that produced a real (non-placeholder) DeckAction or folder.</summary>
    public int MappedKeys { get; set; }
    public List<ElgatoUnmappedEntry> Unmapped { get; set; } = new();
}

/// <summary>POST /streamdeck/elgato/profiles/{id}/import response. Not persisted - the caller decides whether to save it as a preset.</summary>
public sealed class ImportElgatoProfileResponse
{
    public DeckConfig Config { get; set; } = new();
    public ElgatoImportReport Report { get; set; } = new();
}
