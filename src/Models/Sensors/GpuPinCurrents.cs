using System.Collections.Generic;

namespace Nexus.Service.Models.Sensors;

/// <summary>
/// One ROG Astral card's 12V-2x6 connector at its latest reading: the current
/// through each of its six 12V pins in amps, pin 1 first, and when it was read
/// (Unix ms). GpuId is the card's GpuReadout id; Name is the card as the GPU
/// list names it (e.g. "ASUS ROG Astral RTX 5090").
/// </summary>
public sealed record GpuPinCurrents(string GpuId, string Name, IReadOnlyList<float> PinAmps, long ReadAtMs);
