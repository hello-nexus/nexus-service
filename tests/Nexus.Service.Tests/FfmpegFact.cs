using Nexus.Service.Platform;

namespace Nexus.Service.Tests;

/// <summary>A <see cref="FactAttribute"/> skipped when no ffmpeg resolves on this box, so the extraction path is asserted where it can run and never passes vacuously.</summary>
public sealed class FfmpegFactAttribute : FactAttribute
{
    public FfmpegFactAttribute()
    {
        if (FfmpegResolver.Path is null)
        {
            Skip = "ffmpeg is not available on this machine.";
        }
    }
}
