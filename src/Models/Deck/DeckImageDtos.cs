namespace Nexus.Service.Models.Deck;

/// <summary>POST /deck/images response.</summary>
public sealed class DeckImageUploadResponse
{
    public string? Id { get; set; }
    public bool Error { get; set; }
    public string Msg { get; set; } = "Ok";
}
