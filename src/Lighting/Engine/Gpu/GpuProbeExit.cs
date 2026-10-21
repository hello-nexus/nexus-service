namespace Nexus.Service.Lighting.Engine.Gpu;

internal enum GpuProbeVerdict
{
    Works,
    Failed,
    Inconclusive,
}

internal static class GpuProbeExit
{
    /// <summary>Printed by <c>--gpu-probe</c> on stdout immediately before it
    /// touches the GPU stack. Its presence is what separates "the card killed the
    /// child" from "the child died on its way there".</summary>
    public const string EnterMarker = "GLINIT_ENTER";

    /// <summary>
    /// Verdict on a <c>--gpu-probe</c> child. Exit 1 is an init that threw, 2 one
    /// still running past the child's own budget. Any other nonzero code is the
    /// OS killing it (an NTSTATUS such as 0xC0000409 reads as a negative int32),
    /// which condemns the card only if the child reached the marker; without it
    /// the death is unrelated to the GPU. Exit 2 stays inconclusive even with the
    /// marker, because a slow card outruns the child budget and still lands in
    /// the service's wider one.
    /// </summary>
    /// <summary>Leaving a working WARP context takes a probe that actually passed; a failed or inconclusive one leaves it running.</summary>
    public static bool AllowsWarpUpgrade(GpuProbeVerdict verdict) => verdict == GpuProbeVerdict.Works;

    public static GpuProbeVerdict Verdict(int exitCode, bool sawEnterMarker, bool timedOut)
    {
        if (timedOut)
        {
            return GpuProbeVerdict.Inconclusive;
        }
        return exitCode switch
        {
            0 => GpuProbeVerdict.Works,
            1 => GpuProbeVerdict.Failed,
            2 => GpuProbeVerdict.Inconclusive,
            _ => sawEnterMarker ? GpuProbeVerdict.Failed : GpuProbeVerdict.Inconclusive,
        };
    }
}
