namespace Nexus.Service.Cooling;

/// <summary>Wording of the thermal guard's notices, in one place so a simulated notice (dev tools) can never drift from the real one.</summary>
internal static class ThermalGuardNotices
{
    public const string TripTitle = "CPU thermal guard tripped";
    public const string LatchedTitle = "Cooling mode held";
    public const string LatchedText = "The cooling engine kept stalling, so Nexus is holding your fans at full speed.";
    public const string HealedTitle = "Cooling config repaired";

    public static string TripText(double peakC, string reason)
    {
        var cause = reason == ThermalTripReasons.CoolingLoss ? "cooling loss" : "temperature limit";
        return $"CPU reached {peakC:0} C ({cause}). Fans forced to full speed.";
    }

    public static string HealedText(int channels) =>
        $"{channels} fan channel(s) could stop while the CPU is hot and now have a CPU safety curve.";
}
