namespace Nexus.Service.Peripherals.LianLiFlex;

/// <summary>A screen connection that can read its firmware string.</summary>
public interface IFlexScreenVersion
{
    string? ReadVersion();
}
