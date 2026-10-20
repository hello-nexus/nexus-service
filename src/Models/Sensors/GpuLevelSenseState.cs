namespace Nexus.Service.Models.Sensors;

/// <summary>
/// One ROG Astral card's Level Sense: whether a calibration is being taken right
/// now, when the one in use was taken (Unix ms; null when none was saved yet), and
/// the latest reading against it in degrees (null until the card has a calibration
/// and a reading). Sag and Lean are the tilt with a direction (+ sag: the free end
/// sits lower; + lean: the top edge turns toward the fans); X and Y are the same
/// tilt as GPU Tweak III shows it. GpuId is the card's GpuReadout id; Name is the
/// card as the GPU list names it (e.g. "ASUS ROG Astral RTX 5090").
/// </summary>
public sealed record GpuLevelSenseState(
    string GpuId,
    string Name,
    bool Calibrating,
    long? CalibratedAtMs,
    float? Sag = null,
    float? Lean = null,
    float? X = null,
    float? Y = null);
