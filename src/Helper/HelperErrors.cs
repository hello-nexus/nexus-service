namespace Nexus.Service.Helper;

public static class HelperErrors
{
    /// <summary>The <see cref="HelperResult.Error"/> of a command the helper never answered in time.</summary>
    public const string Timeout = "timeout";

    /// <summary>The caller's token fired while the command was in flight.</summary>
    public const string Canceled = "canceled";
}
