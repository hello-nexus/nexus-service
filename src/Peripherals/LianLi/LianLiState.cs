namespace Nexus.Service.Peripherals.LianLi;

public sealed class LianLiState
{
    /// <summary>Last decoded RPM per port (0..3). -1 = no data yet.</summary>
    public int[] Rpm { get; } = new int[] { -1, -1, -1, -1 };

    /// <summary>Fan duty percent per port.</summary>
    public int[] Duty { get; } = new int[LianLiProtocol.PortCount];

    public bool IsConnected { get; set; }

    /// <summary>Hub firmware version read on attach, as major.minor (raw hex when the encoding is unknown); empty until read.</summary>
    public string FirmwareVersion { get; set; } = "";

    /// <summary>The hub has given a real version: empty and the all-ones byte a hub reports while it still starts up do not count.</summary>
    public bool FirmwareAnswered => !string.IsNullOrEmpty(FirmwareVersion) && FirmwareVersion != "0xFF";
}
