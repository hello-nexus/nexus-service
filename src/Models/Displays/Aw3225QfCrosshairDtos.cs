namespace Nexus.Service.Models.Displays;

public sealed class Aw3225QfCrosshairConfig
{
    public int Type { get; set; }
    public int Color { get; set; } = 2;
    public int MaskControl { get; set; }
}

public sealed class Aw3225QfCrosshairRequest
{
    public bool Enabled { get; set; }
    public int Type { get; set; }
    public int Color { get; set; } = 2;
    public int MaskControl { get; set; }
}

public sealed class Aw3225QfCrosshairStatus
{
    public bool Connected { get; set; }
    public bool Enabled { get; set; }
    public int ActiveEngine { get; set; }
    public string DisplayId { get; set; } = "";
    public Aw3225QfCrosshairConfig Config { get; set; } = new();
    public string Error { get; set; } = "";
}
