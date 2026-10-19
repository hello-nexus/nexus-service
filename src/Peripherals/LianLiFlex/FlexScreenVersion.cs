using System.Text;

namespace Nexus.Service.Peripherals.LianLiFlex;

/// <summary>A screen connection that can read its firmware string.</summary>
public interface IFlexScreenVersion
{
    string? ReadVersion();
}

public static class FlexScreenVersion
{
    /// <summary>The firmware string of a version reply: ASCII at [8..39], up to the first zero byte.</summary>
    public static string Parse(byte[] reply)
    {
        if (reply.Length < 9)
        {
            return "";
        }
        var end = System.Math.Min(40, reply.Length);
        var len = 0;
        while (8 + len < end && reply[8 + len] != 0)
        {
            len++;
        }
        return Encoding.ASCII.GetString(reply, 8, len).Trim();
    }
}
