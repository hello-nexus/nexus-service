namespace Nexus.Service.Models.Deck;

/// <summary>POST /deck/images response.</summary>
public sealed class DeckImageUploadResponse
{
    public string? Id { get; set; }
    public bool Error { get; set; }
    public string Msg { get; set; } = "Ok";
}

/// <summary>POST /deck/images/stage/{stageId} JSON body; the same fields may instead be sent as multipart form fields.</summary>
public sealed class DeckImageStageRequest
{
    public string? Crop { get; set; }
}
