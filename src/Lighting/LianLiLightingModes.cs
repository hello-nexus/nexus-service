using System;
using System.Collections.Generic;
using Nexus.Service.Peripherals.LianLi;

namespace Nexus.Service.Lighting;

public sealed class LianLiModeInfo
{
    public LianLiModeInfo(
        string key, string label, byte effect,
        bool speed = false, bool direction = false, string[]? colors = null, byte merged = 0, bool wholeFan = false, bool corners = false)
    {
        Key = key;
        Label = label;
        EffectByte = effect;
        HasSpeed = speed;
        HasDirection = direction;
        DefaultColors = colors ?? Array.Empty<string>();
        MergedEffectByte = merged;
        WholeFan = wholeFan;
        CornerPalette = corners;
    }

    public string Key { get; }
    public string Label { get; }
    /// <summary>The family's firmware effect byte for this mode.</summary>
    public byte EffectByte { get; }
    public bool HasSpeed { get; }
    public bool HasDirection { get; }
    public bool HasBrightness => true;
    public int ColorsMin => 0;
    public int ColorsMax => DefaultColors.Count;
    /// <summary>Palette the mode starts from; also used while the user has picked no colours.</summary>
    public IReadOnlyList<string> DefaultColors { get; }
    /// <summary>Effect byte of the across-every-port variant; 0 when the mode has none.</summary>
    public byte MergedEffectByte { get; }
    /// <summary>On a two-ring hub the firmware animates both rings from one commit on the port's inner channel; the outer channel's byte space means a different effect.</summary>
    public bool WholeFan { get; }
    /// <summary>The palette colours the four sides of each fan's ring in turn instead of filling slots.</summary>
    public bool CornerPalette { get; }

    public bool MergesOn(in LianLiFanProfile profile) => MergedEffectByte != 0 && profile.SupportsMerge;
}

public static class LianLiLightingModes
{
    // speed index 0..4 -> firmware byte; 0x02=slowest, 0xFE=fastest
    public static readonly byte[] SpeedCodes = { 0x02, 0x01, 0x00, 0xFF, 0xFE };

    // brightness index 0..4 -> firmware byte; 0x08=off, 0x00=full (inverted scale)
    public static readonly byte[] BrightnessCodes = { 0x08, 0x03, 0x02, 0x01, 0x00 };

    // direction 0=LTR(0x00), 1=RTL(0x01)
    public static byte DirectionByte(int direction) => direction == 1 ? (byte)0x01 : (byte)0x00;

    /// <summary>Host-streamed per-LED frames, committed as the static effect.</summary>
    public static readonly LianLiModeInfo Custom = new("custom", "Custom (per-LED effects)", LianLiProtocol.EffectStatic);

    // Effect bytes differ per family firmware; a key missing from a family's
    // list is a mode that firmware lacks.
    private static readonly Dictionary<LianLiFanFamily, LianLiModeInfo[]> Catalogs = new()
    {
        [LianLiFanFamily.Sl] = new LianLiModeInfo[]
        {
            Custom,
            new("rainbowWave", "Rainbow Wave", 0x05, speed: true, direction: true),
            new("spectrumCycle", "Spectrum Cycle", 0x04, speed: true),
            new("static", "Static", 0x01, colors: new[] { "#00D7FF", "#FF0000", "#00FF00", "#96FF00" }),
            new("breathing", "Breathing", 0x02, speed: true, colors: new[] { "#00D7FF", "#FF0000", "#00FF00", "#96FF00" }),
            new("colorCycle", "Color Cycle", 0x23, speed: true, direction: true, colors: new[] { "#FF0000", "#00FFAA", "#00FF00" }),
            new("runway", "Runway", 0x1C, speed: true, colors: new[] { "#FF0000", "#00D7FF" }),
            new("staggered", "Staggered", 0x18, speed: true, colors: new[] { "#00D7FF", "#FF0000" }),
            new("tide", "Tide", 0x1A, speed: true, colors: new[] { "#00D7FF", "#FF0000" }),
            new("meteor", "Meteor", 0x24, speed: true, colors: new[] { "#00D7FF", "#FF0000" }),
            new("mixing", "Mixing", 0x1E, speed: true, colors: new[] { "#00D7FF", "#FF0000" }),
            new("stack", "Stack", 0x20, speed: true, direction: true, colors: new[] { "#FF0000" }),
            new("stackMultiColor", "Stack Multi Color", 0x21, speed: true, direction: true),
            new("neon", "Neon", 0x22, speed: true),
        },
        [LianLiFanFamily.Al] = new LianLiModeInfo[]
        {
            Custom,
            new("rainbowWave", "Rainbow Wave", 0x28, speed: true, direction: true, wholeFan: true),
            new("spectrumCycle", "Spectrum Cycle", 0x35, speed: true, wholeFan: true),
            new("static", "Static", 0x01, colors: new[] { "#00D7FF", "#FF0000", "#00FF00", "#96FF00" }),
            new("breathing", "Breathing", 0x02, speed: true, colors: new[] { "#FF0000", "#FF6900", "#96FF00", "#00FF00" }),
            new("taichi", "Taichi", 0x2C, speed: true, direction: true, colors: new[] { "#FF0000", "#00FF00" }, wholeFan: true),
            new("colorCycle", "Color Cycle", 0x2B, speed: true, direction: true, colors: new[] { "#FF0000", "#00FFAA", "#00FF00", "#96FF00" }, wholeFan: true),
            new("runway", "Runway", 0x1A, speed: true, colors: new[] { "#00D7FF", "#FF0000" }),
            new("meteor", "Meteor", 0x19, speed: true, direction: true, colors: new[] { "#00D7FF", "#FF0000", "#00FF00", "#96FF00" }),
            new("warning", "Warning", 0x2D, speed: true, colors: new[] { "#FF0000", "#FF6900", "#00FF00", "#00D7FF" }, wholeFan: true),
            new("voice", "Voice", 0x2E, speed: true, direction: true, colors: new[] { "#00D7FF", "#FF0000", "#00FF00", "#96FF00" }, wholeFan: true),
            new("spanningTeacups", "Spanning Teacups", 0x38, speed: true, direction: true, colors: new[] { "#00D7FF", "#FF0000", "#00FF00", "#96FF00" }, wholeFan: true),
            new("tornado", "Tornado", 0x36, speed: true, direction: true, colors: new[] { "#00D7FF", "#FF0000", "#00FF00", "#96FF00" }, wholeFan: true),
            new("mixing", "Mixing", 0x2F, speed: true, colors: new[] { "#00D7FF", "#FF0000" }, wholeFan: true),
            new("stack", "Stack", 0x30, speed: true, direction: true, colors: new[] { "#00D7FF", "#FF0000" }, wholeFan: true),
            new("staggered", "Staggered", 0x37, speed: true, colors: new[] { "#FF0000", "#00FFAA", "#00FF00", "#96FF00" }, wholeFan: true),
            new("tide", "Tide", 0x31, speed: true, colors: new[] { "#00D7FF", "#FF0000", "#00FF00", "#96FF00" }, wholeFan: true),
            new("scan", "Scan", 0x32, speed: true, colors: new[] { "#00FFAA", "#FF0000" }, wholeFan: true),
            new("contest", "Contest", 0x33, speed: true, direction: true, colors: new[] { "#00D7FF", "#FF0000", "#00FF00" }, wholeFan: true),
        },
        [LianLiFanFamily.SlInfinity] = new LianLiModeInfo[]
        {
            Custom,
            new("rainbowWave", "Rainbow Wave", 0x05, speed: true, direction: true),
            new("spectrumCycle", "Spectrum Cycle", 0x04, speed: true),
            new("static", "Static", 0x01, colors: new[] { "#FF0000", "#0000FF", "#00FF55", "#FF6900" }),
            new("breathing", "Breathing", 0x02, speed: true, colors: new[] { "#FF0000", "#0000FF", "#00FF55", "#FF6900" }),
            new("breathingRainbow", "Breathing Rainbow", 0x06, speed: true),
            new("colorCycle", "Color Cycle", 0x18, speed: true, direction: true, colors: new[] { "#FF0000", "#0000FF", "#00FF55", "#FF6900" }),
            new("runway", "Runway", 0x1A, speed: true, colors: new[] { "#FF0000", "#0000FF" }, merged: 0x46),
            new("mopUp", "Mop Up", 0x44, speed: true, colors: new[] { "#FF0000", "#0000FF", "#00FF55", "#FF6900" }, merged: 0x47),
            new("meteor", "Meteor", 0x19, speed: true, direction: true, colors: new[] { "#FF0000", "#0000FF", "#00FF55", "#FF6900" }),
            new("warning", "Warning", 0x36, speed: true, colors: new[] { "#FF0000", "#0000FF", "#00FF55", "#FF6900" }),
            new("voice", "Voice", 0x2A, speed: true, direction: true, colors: new[] { "#FF0000", "#0000FF" }),
            new("mixing", "Mixing", 0x38, speed: true, colors: new[] { "#FF0000", "#0000FF" }, merged: 0x48),
            new("stack", "Stack", 0x39, speed: true, direction: true, colors: new[] { "#FF0000", "#0000FF" }, merged: 0x49),
            new("tide", "Tide", 0x3A, speed: true, colors: new[] { "#FF0000", "#0000FF", "#00FF55", "#FF6900" }, merged: 0x4A),
            new("scan", "Scan", 0x3B, speed: true, colors: new[] { "#FF0000", "#0000FF" }, merged: 0x4B),
            new("door", "Door", 0x3C, speed: true, colors: new[] { "#FF0000", "#0000FF", "#00FF55", "#FF6900" }, merged: 0x4C),
            new("heartBeat", "Heart Beat", 0x42, speed: true, colors: new[] { "#FF0000" }),
            new("heartBeatRunway", "Heart Beat Runway", 0x43, speed: true, direction: true, colors: new[] { "#FF0000" }, merged: 0x4D),
            new("disco", "Disco", 0x23, speed: true, direction: true, colors: new[] { "#0000FF", "#FF6900", "#0000FF", "#FF6900" }),
            new("electricCurrent", "Electric Current", 0x41, speed: true, colors: new[] { "#FFFFFF", "#FFFFFF", "#FFFFFF", "#FFFFFF" }, merged: 0x4E),
        },
        [LianLiFanFamily.SlV2] = new LianLiModeInfo[]
        {
            Custom,
            new("rainbowWave", "Rainbow Wave", 0x05, speed: true, direction: true),
            new("spectrumCycle", "Spectrum Cycle", 0x04, speed: true),
            new("static", "Static", 0x01, colors: new[] { "#00D7FF", "#FF0000", "#00FF00", "#96FF00", "#FF6900", "#FFD700" }),
            new("breathing", "Breathing", 0x02, speed: true, colors: new[] { "#00D7FF", "#FF0000", "#00FF00", "#96FF00", "#FF6900", "#FFD700" }),
            new("colorCycle", "Color Cycle", 0x23, speed: true, direction: true, colors: new[] { "#FF0000", "#00FFAA", "#00FF00" }),
            new("runway", "Runway", 0x1C, speed: true, colors: new[] { "#FF0000", "#00D7FF" }),
            new("staggered", "Staggered", 0x18, speed: true, colors: new[] { "#00D7FF", "#FF0000" }),
            new("tide", "Tide", 0x1A, speed: true, colors: new[] { "#00D7FF", "#FF0000" }),
            new("meteor", "Meteor", 0x24, speed: true, colors: new[] { "#00D7FF", "#FF0000" }),
            new("mixing", "Mixing", 0x1E, speed: true, colors: new[] { "#00D7FF", "#FF0000" }),
            new("stack", "Stack", 0x20, speed: true, direction: true, colors: new[] { "#FF0000" }),
            new("stackMultiColor", "Stack Multi Color", 0x21, speed: true, direction: true),
            new("neon", "Neon", 0x22, speed: true),
            new("voice", "Voice", 0x26, speed: true),
            new("groove", "Groove", 0x27, speed: true, direction: true, colors: new[] { "#FF0000" }),
            new("render", "Render", 0x28, speed: true, direction: true, colors: new[] { "#FF0096", "#00D7FF", "#FF6900", "#96FF00" }),
            new("tunnel", "Tunnel", 0x29, speed: true, colors: new[] { "#FF0096", "#00D7FF", "#FF6900", "#96FF00" }),
        },
        [LianLiFanFamily.AlV2] = new LianLiModeInfo[]
        {
            Custom,
            new("rainbowWave", "Rainbow Wave", 0x2B, speed: true, direction: true, wholeFan: true),
            new("spectrumCycle", "Spectrum Cycle", 0x04, speed: true),
            new("static", "Static", 0x01, colors: new[] { "#00D7FF", "#FF0000", "#00FF00", "#96FF00", "#FF6900", "#FFD700" }),
            new("breathing", "Breathing", 0x02, speed: true, colors: new[] { "#00D7FF", "#FF0000", "#00FF00", "#96FF00", "#FF6900", "#FFD700" }),
            new("taichi", "Taichi", 0x2F, speed: true, direction: true, colors: new[] { "#FF0000", "#00FF00" }, wholeFan: true),
            new("colorCycle", "Color Cycle", 0x2E, speed: true, direction: true, colors: new[] { "#FF0000", "#00FFAA", "#00FF00", "#96FF00" }, wholeFan: true),
            new("runway", "Runway", 0x1A, speed: true, colors: new[] { "#00D7FF", "#FF0000" }),
            new("mopUp", "Mop Up", 0x3E, speed: true, colors: new[] { "#00D7FF", "#FF0000" }, wholeFan: true),
            new("meteor", "Meteor", 0x19, speed: true, direction: true, colors: new[] { "#00D7FF", "#FF0000", "#00FF00", "#96FF00" }),
            new("wave", "Wave", 0x3B, speed: true, colors: new[] { "#FF0000" }, wholeFan: true),
            new("spring", "Spring", 0x3C, speed: true, direction: true, colors: new[] { "#00D7FF", "#FF0000", "#00FF00", "#96FF00" }, wholeFan: true),
            new("tailChasing", "Tail Chasing", 0x3D, speed: true, direction: true, colors: new[] { "#00D7FF", "#FF0000", "#00FF00", "#96FF00" }, wholeFan: true),
            new("warning", "Warning", 0x30, speed: true, colors: new[] { "#FF0000", "#FF6900", "#00FF00", "#00D7FF" }, wholeFan: true),
            new("voice", "Voice", 0x31, speed: true, direction: true, colors: new[] { "#00D7FF", "#FF0000", "#00FF00", "#96FF00" }, wholeFan: true),
            new("spanningTeacups", "Spanning Teacups", 0x41, speed: true, direction: true, colors: new[] { "#00D7FF", "#FF0000", "#00FF00", "#96FF00" }, wholeFan: true),
            new("tornado", "Tornado", 0x3F, speed: true, direction: true, colors: new[] { "#00D7FF", "#FF0000", "#00FF00", "#96FF00" }, wholeFan: true),
            new("mixing", "Mixing", 0x32, speed: true, colors: new[] { "#00D7FF", "#FF0000" }, wholeFan: true),
            new("stack", "Stack", 0x43, speed: true, direction: true, colors: new[] { "#00D7FF", "#FF0000" }, wholeFan: true),
            new("staggered", "Staggered", 0x40, speed: true, colors: new[] { "#FF0000", "#00FFAA", "#00FF00", "#96FF00" }, wholeFan: true),
            new("tide", "Tide", 0x33, speed: true, colors: new[] { "#00D7FF", "#FF0000", "#00FF00", "#96FF00" }, wholeFan: true),
            new("scan", "Scan", 0x34, speed: true, colors: new[] { "#00FFAA", "#FF0000" }, wholeFan: true),
            new("contest", "Contest", 0x35, speed: true, direction: true, colors: new[] { "#00D7FF", "#FF0000", "#00FF00" }, wholeFan: true),
            new("colorfulCity", "Colorful City", 0x38, speed: true, wholeFan: true),
            new("render", "Render", 0x39, speed: true, direction: true, colors: new[] { "#FF0096", "#00D7FF", "#FF6900", "#96FF00" }, wholeFan: true),
            new("electricCurrent", "Electric Current", 0x42, speed: true, colors: new[] { "#FFFFFF", "#FFFFFF", "#FFFFFF", "#FFFFFF" }, wholeFan: true),
            new("twinkle", "Twinkle", 0x3A, speed: true, wholeFan: true),
        },

    };

    // A two-ring family's per-ring effects: the inner channel and the outer
    // channel read the same effect byte as different animations.
    private static readonly Dictionary<LianLiFanFamily, LianLiModeInfo[]> InnerRingCatalogs = new()
    {
        [LianLiFanFamily.Al] = new LianLiModeInfo[]
        {
            new("rainbowWave", "Rainbow Wave", 0x05, speed: true, direction: true),
            new("static", "Static", 0x01, colors: new[] { "#00D7FF", "#FF0000", "#00FF00", "#96FF00" }),
            new("breathing", "Breathing", 0x02, speed: true, colors: new[] { "#00D7FF", "#FF0000", "#00FF00", "#96FF00" }),
            new("colorCycle", "Color Cycle", 0x18, speed: true, direction: true, colors: new[] { "#00D7FF", "#FF0000", "#00FF00", "#96FF00" }),
            new("runway", "Runway", 0x1A, speed: true, colors: new[] { "#00D7FF", "#FF0000" }),
            new("mopUp", "Mop Up", 0x1B, speed: true, colors: new[] { "#00D7FF", "#FF0000" }),
            new("pacMan", "Chomper", 0x27, speed: true, direction: true, colors: new[] { "#00D7FF", "#FF0000" }),
            new("meteor", "Meteor", 0x19, speed: true, direction: true, colors: new[] { "#00D7FF", "#FF0000", "#00FF00", "#96FF00" }),
            new("meteorRainbow", "Meteor Rainbow", 0x08, speed: true, direction: true),
            new("lottery", "Lottery", 0x1D, speed: true, direction: true, colors: new[] { "#00D7FF", "#FF0000" }),
            new("wave", "Wave", 0x1E, speed: true, colors: new[] { "#FF0000" }),
            new("spring", "Spring", 0x1F, speed: true, direction: true, colors: new[] { "#00D7FF", "#FF0000", "#00FF00", "#96FF00" }),
            new("tailChasing", "Tail Chasing", 0x20, speed: true, direction: true, colors: new[] { "#00D7FF", "#FF0000", "#00FF00", "#96FF00" }),
            new("warning", "Warning", 0x21, speed: true, colors: new[] { "#00D7FF", "#FF0000", "#00FF00", "#96FF00" }),
            new("voice", "Voice", 0x22, speed: true, direction: true, colors: new[] { "#00D7FF", "#FF0000", "#00FF00", "#96FF00" }),
            new("mixing", "Mixing", 0x23, speed: true, colors: new[] { "#00D7FF", "#FF0000" }),
            new("stack", "Stack", 0x24, speed: true, direction: true, colors: new[] { "#00D7FF", "#FF0000" }),
            new("tide", "Tide", 0x25, speed: true, colors: new[] { "#00D7FF", "#FF0000", "#00FF00", "#96FF00" }),
            new("scan", "Scan", 0x26, speed: true, colors: new[] { "#00D7FF" }),
        },
        [LianLiFanFamily.SlInfinity] = new LianLiModeInfo[]
        {
            new("rainbowWave", "Rainbow Wave", 0x05, speed: true, direction: true),
            new("spectrumCycle", "Spectrum Cycle", 0x04, speed: true),
            new("static", "Static", 0x01, colors: new[] { "#FF0000", "#0000FF", "#00FF55", "#FF6900" }),
            new("breathing", "Breathing", 0x02, speed: true, colors: new[] { "#FF0000", "#0000FF", "#00FF55", "#FF6900" }),
            new("breathingRainbow", "Breathing Rainbow", 0x06, speed: true),
            new("taichi", "Taichi", 0x1C, speed: true, direction: true, colors: new[] { "#FF0000", "#0000FF" }),
            new("colorCycle", "Color Cycle", 0x18, speed: true, direction: true, colors: new[] { "#FF0000", "#0000FF", "#00FF55", "#FF6900" }),
            new("runway", "Runway", 0x1A, speed: true, colors: new[] { "#FF0000", "#0000FF" }),
            new("mopUp", "Mop Up", 0x1B, speed: true, colors: new[] { "#FF0000", "#0000FF", "#00FF55", "#FF6900" }),
            new("meteor", "Meteor", 0x19, speed: true, direction: true, colors: new[] { "#FF0000", "#0000FF", "#00FF55", "#FF6900" }),
            new("meteorRainbow", "Meteor Rainbow", 0x08, speed: true, direction: true),
            new("lottery", "Lottery", 0x26, speed: true, direction: true, colors: new[] { "#FF0000", "#0000FF" }),
            new("warning", "Warning", 0x29, speed: true, colors: new[] { "#FF0000", "#0000FF", "#00FF55", "#FF6900" }),
            new("voice", "Voice", 0x2A, speed: true, direction: true, colors: new[] { "#FF0000", "#0000FF", "#00FF55", "#FF6900" }),
            new("mixing", "Mixing", 0x2B, speed: true, colors: new[] { "#FF0000", "#0000FF" }),
            new("stack", "Stack", 0x2C, speed: true, direction: true, colors: new[] { "#FF0000", "#0000FF" }),
            new("tide", "Tide", 0x2D, speed: true, colors: new[] { "#FF0000", "#0000FF", "#00FF55", "#FF6900" }),
            new("scan", "Scan", 0x2E, speed: true, colors: new[] { "#FF0000" }),
            new("doubleMeteor", "Double Meteor", 0x1D, speed: true, colors: new[] { "#FF0000", "#0000FF", "#00FF55", "#FF6900" }),
            new("meteorContest", "Meteor Contest", 0x1E, speed: true, direction: true, colors: new[] { "#FF0000", "#0000FF" }),
            new("meteorMix", "Meteor Mix", 0x1F, speed: true, colors: new[] { "#FF0000", "#0000FF" }),
            new("returnArc", "Return Arc", 0x20, speed: true, direction: true, colors: new[] { "#FF0000", "#0000FF", "#00FF55", "#FF6900" }),
            new("doubleArc", "Double Arc", 0x21, speed: true, direction: true, colors: new[] { "#FF0000", "#0000FF", "#00FF55", "#FF6900" }),
            new("door", "Door", 0x22, speed: true, colors: new[] { "#FF0000", "#0000FF", "#00FF55", "#FF6900" }),
            new("heartBeat", "Heart Beat", 0x24, speed: true, colors: new[] { "#FF0000" }),
            new("heartBeatRunway", "Heart Beat Runway", 0x45, speed: true, direction: true, colors: new[] { "#FF0000" }),
            new("disco", "Disco", 0x23, speed: true, direction: true, colors: new[] { "#0000FF", "#FF6900", "#0000FF", "#FF6900" }),
        },
        [LianLiFanFamily.AlV2] = new LianLiModeInfo[]
        {
            new("rainbowWave", "Rainbow Wave", 0x05, speed: true, direction: true),
            new("spectrumCycle", "Spectrum Cycle", 0x04, speed: true),
            new("static", "Static", 0x01, colors: new[] { "#00D7FF", "#FF0000", "#00FF00", "#96FF00", "#FF6900", "#FFD700" }),
            new("breathing", "Breathing", 0x02, speed: true, colors: new[] { "#00D7FF", "#FF0000", "#00FF00", "#96FF00", "#FF6900", "#FFD700" }),
            new("colorCycle", "Color Cycle", 0x18, speed: true, direction: true, colors: new[] { "#00D7FF", "#FF0000", "#00FF00", "#96FF00" }),
            new("runway", "Runway", 0x1A, speed: true, colors: new[] { "#00D7FF", "#FF0000" }),
            new("mopUp", "Mop Up", 0x1B, speed: true, colors: new[] { "#00D7FF", "#FF0000" }),
            new("pacMan", "Chomper", 0x27, speed: true, direction: true, colors: new[] { "#00D7FF", "#FF0000" }),
            new("meteor", "Meteor", 0x19, speed: true, direction: true, colors: new[] { "#00D7FF", "#FF0000", "#00FF00", "#96FF00" }),
            new("meteorRainbow", "Meteor Rainbow", 0x08, speed: true, direction: true),
            new("lottery", "Lottery", 0x1D, speed: true, direction: true, colors: new[] { "#00D7FF", "#FF0000" }),
            new("wave", "Wave", 0x1E, speed: true, colors: new[] { "#FF0000" }),
            new("spring", "Spring", 0x1F, speed: true, direction: true, colors: new[] { "#00D7FF", "#FF0000", "#00FF00", "#96FF00" }),
            new("tailChasing", "Tail Chasing", 0x20, speed: true, direction: true, colors: new[] { "#00D7FF", "#FF0000", "#00FF00", "#96FF00" }),
            new("warning", "Warning", 0x21, speed: true, colors: new[] { "#00D7FF", "#FF0000", "#00FF00", "#96FF00" }),
            new("voice", "Voice", 0x22, speed: true, direction: true, colors: new[] { "#00D7FF", "#FF0000", "#00FF00", "#96FF00" }),
            new("mixing", "Mixing", 0x23, speed: true, colors: new[] { "#00D7FF", "#FF0000" }),
            new("stack", "Stack", 0x24, speed: true, direction: true, colors: new[] { "#00D7FF", "#FF0000" }),
            new("tide", "Tide", 0x25, speed: true, colors: new[] { "#00D7FF", "#FF0000", "#00FF00", "#96FF00" }),
            new("scan", "Scan", 0x26, speed: true, colors: new[] { "#00D7FF" }),
            new("colorfulCity", "Colorful City", 0x28, speed: true),
            new("render", "Render", 0x29, speed: true, direction: true, colors: new[] { "#FF0096", "#00D7FF", "#FF6900", "#96FF00" }),
            new("twinkle", "Twinkle", 0x2A, speed: true),
        },
    };

    private static readonly Dictionary<LianLiFanFamily, LianLiModeInfo[]> OuterRingCatalogs = new()
    {
        [LianLiFanFamily.Al] = new LianLiModeInfo[]
        {
            new("rainbowWave", "Rainbow Wave", 0x05, speed: true, direction: true),
            new("static", "Static", 0x01, colors: new[] { "#00D7FF", "#FF0000", "#00FF00", "#96FF00" }),
            new("staticColorful", "Static Colorful", 0x01, corners: true, colors: new[] { "#00D7FF", "#FF0000", "#00FF00", "#96FF00" }),
            new("breathing", "Breathing", 0x02, speed: true, colors: new[] { "#00D7FF", "#FF0000", "#00FF00", "#96FF00" }),
            new("breathingColorful", "Breathing Colorful", 0x02, speed: true, corners: true, colors: new[] { "#00D7FF", "#FF0000", "#00FF00", "#96FF00" }),
            new("breathingRainbow", "Breathing Rainbow", 0x06, speed: true),
            new("colorCycle", "Color Cycle", 0x18, speed: true, direction: true, colors: new[] { "#00D7FF", "#FF0000", "#00FF00", "#96FF00" }),
            new("runway", "Runway", 0x1A, speed: true, colors: new[] { "#00D7FF", "#FF0000" }),
            new("mopUp", "Mop Up", 0x1B, speed: true, colors: new[] { "#00D7FF", "#FF0000" }),
            new("meteor", "Meteor", 0x19, speed: true, direction: true, colors: new[] { "#00D7FF", "#FF0000", "#00FF00", "#96FF00" }),
            new("meteorRainbow", "Meteor Rainbow", 0x08, speed: true, direction: true),
            new("lottery", "Lottery", 0x1D, speed: true, direction: true, colors: new[] { "#00D7FF", "#FF0000" }),
            new("wave", "Wave", 0x1E, speed: true, colors: new[] { "#FF0000" }),
            new("spring", "Spring", 0x1F, speed: true, direction: true, colors: new[] { "#00D7FF", "#FF0000", "#00FF00", "#96FF00" }),
            new("tailChasing", "Tail Chasing", 0x20, speed: true, direction: true, colors: new[] { "#00D7FF", "#FF0000", "#00FF00", "#96FF00" }),
            new("warning", "Warning", 0x21, speed: true, colors: new[] { "#00D7FF", "#FF0000", "#00FF00", "#96FF00" }),
            new("voice", "Voice", 0x22, speed: true, direction: true, colors: new[] { "#00D7FF", "#FF0000", "#00FF00", "#96FF00" }),
            new("mixing", "Mixing", 0x23, speed: true, colors: new[] { "#00D7FF", "#FF0000" }),
            new("stack", "Stack", 0x24, speed: true, direction: true, colors: new[] { "#00D7FF", "#FF0000" }),
            new("tide", "Tide", 0x25, speed: true, colors: new[] { "#00D7FF", "#FF0000", "#00FF00", "#96FF00" }),
            new("scan", "Scan", 0x26, speed: true, colors: new[] { "#00D7FF" }),
        },
        [LianLiFanFamily.SlInfinity] = new LianLiModeInfo[]
        {
            new("rainbowWave", "Rainbow Wave", 0x05, speed: true, direction: true),
            new("spectrumCycle", "Spectrum Cycle", 0x04, speed: true),
            new("static", "Static", 0x01, colors: new[] { "#FF0000", "#0000FF", "#00FF55", "#FF6900" }),
            new("breathing", "Breathing", 0x02, speed: true, colors: new[] { "#FF0000", "#0000FF", "#00FF55", "#FF6900" }),
            new("breathingRainbow", "Breathing Rainbow", 0x06, speed: true),
            new("colorCycle", "Color Cycle", 0x18, speed: true, direction: true, colors: new[] { "#FF0000", "#0000FF", "#00FF55", "#FF6900" }),
            new("runway", "Runway", 0x1A, speed: true, colors: new[] { "#FF0000", "#0000FF" }),
            new("mopUp", "Mop Up", 0x1B, speed: true, colors: new[] { "#FF0000", "#0000FF", "#00FF55", "#FF6900" }),
            new("meteor", "Meteor", 0x19, speed: true, direction: true, colors: new[] { "#FF0000", "#0000FF", "#00FF55", "#FF6900" }),
            new("meteorRainbow", "Meteor Rainbow", 0x08, speed: true, direction: true),
            new("colorfulMeteor", "Colorful Meteor", 0x27, speed: true, direction: true),
            new("lottery", "Lottery", 0x26, speed: true, direction: true, colors: new[] { "#FF0000", "#0000FF" }),
            new("warning", "Warning", 0x29, speed: true, colors: new[] { "#FF0000", "#0000FF", "#00FF55", "#FF6900" }),
            new("voice", "Voice", 0x2A, speed: true, direction: true, colors: new[] { "#FF0000", "#0000FF", "#00FF55", "#FF6900" }),
            new("mixing", "Mixing", 0x2B, speed: true, colors: new[] { "#FF0000", "#0000FF" }),
            new("stack", "Stack", 0x2C, speed: true, direction: true, colors: new[] { "#FF0000", "#0000FF" }),
            new("tide", "Tide", 0x2D, speed: true, colors: new[] { "#FF0000", "#0000FF", "#00FF55", "#FF6900" }),
            new("scan", "Scan", 0x2E, speed: true, colors: new[] { "#FF0000" }),
            new("door", "Door", 0x22, speed: true, colors: new[] { "#FF0000", "#0000FF", "#00FF55", "#FF6900" }),
            new("heartBeat", "Heart Beat", 0x24, speed: true, colors: new[] { "#FF0000" }),
            new("disco", "Disco", 0x23, speed: true, direction: true, colors: new[] { "#0000FF", "#FF6900", "#0000FF", "#FF6900" }),
            new("reflect", "Reflect", 0x30, speed: true, colors: new[] { "#FF0000" }),
        },
        [LianLiFanFamily.AlV2] = new LianLiModeInfo[]
        {
            new("rainbowWave", "Rainbow Wave", 0x05, speed: true, direction: true),
            new("spectrumCycle", "Spectrum Cycle", 0x04, speed: true),
            new("static", "Static", 0x01, colors: new[] { "#00D7FF", "#FF0000", "#00FF00", "#96FF00", "#FF6900", "#FFD700" }),
            new("staticColorful", "Static Colorful", 0x01, corners: true, colors: new[] { "#00D7FF", "#FF0000", "#00FF00", "#96FF00" }),
            new("breathing", "Breathing", 0x02, speed: true, colors: new[] { "#00D7FF", "#FF0000", "#00FF00", "#96FF00", "#FF6900", "#FFD700" }),
            new("breathingColorful", "Breathing Colorful", 0x02, speed: true, corners: true, colors: new[] { "#00D7FF", "#FF0000", "#00FF00", "#96FF00" }),
            new("breathingRainbow", "Breathing Rainbow", 0x06, speed: true),
            new("colorCycle", "Color Cycle", 0x1C, speed: true, direction: true, colors: new[] { "#00D7FF", "#FF0000", "#00FF00", "#96FF00" }),
            new("runway", "Runway", 0x1A, speed: true, colors: new[] { "#00D7FF", "#FF0000" }),
            new("mopUp", "Mop Up", 0x1B, speed: true, colors: new[] { "#00D7FF", "#FF0000" }),
            new("meteor", "Meteor", 0x19, speed: true, direction: true, colors: new[] { "#00D7FF", "#FF0000", "#00FF00", "#96FF00" }),
            new("meteorRainbow", "Meteor Rainbow", 0x08, speed: true, direction: true),
            new("lottery", "Lottery", 0x1D, speed: true, direction: true, colors: new[] { "#00D7FF", "#FF0000" }),
            new("wave", "Wave", 0x1E, speed: true, colors: new[] { "#FF0000" }),
            new("spring", "Spring", 0x1F, speed: true, direction: true, colors: new[] { "#00D7FF", "#FF0000", "#00FF00", "#96FF00" }),
            new("tailChasing", "Tail Chasing", 0x20, speed: true, direction: true, colors: new[] { "#00D7FF", "#FF0000", "#00FF00", "#96FF00" }),
            new("warning", "Warning", 0x21, speed: true, colors: new[] { "#00D7FF", "#FF0000", "#00FF00", "#96FF00" }),
            new("voice", "Voice", 0x22, speed: true, direction: true, colors: new[] { "#00D7FF", "#FF0000", "#00FF00", "#96FF00" }),
            new("mixing", "Mixing", 0x23, speed: true, colors: new[] { "#00D7FF", "#FF0000" }),
            new("stack", "Stack", 0x24, speed: true, direction: true, colors: new[] { "#00D7FF", "#FF0000" }),
            new("tide", "Tide", 0x25, speed: true, colors: new[] { "#00D7FF", "#FF0000", "#00FF00", "#96FF00" }),
            new("scan", "Scan", 0x26, speed: true, colors: new[] { "#00D7FF" }),
            new("colorfulCity", "Colorful City", 0x28, speed: true),
            new("render", "Render", 0x29, speed: true, direction: true, colors: new[] { "#FF0096", "#00D7FF", "#FF6900", "#96FF00" }),
            new("twinkle", "Twinkle", 0x2A, speed: true),
        },
    };

    /// <summary>The modes one ring of a two-ring family accepts; empty for a family without per-ring effects.</summary>
    public static IReadOnlyList<LianLiModeInfo> RingCatalogFor(LianLiFanFamily family, bool outer) =>
        (outer ? OuterRingCatalogs : InnerRingCatalogs).TryGetValue(family, out var list) ? list : Array.Empty<LianLiModeInfo>();

    public static LianLiModeInfo? FindRing(LianLiFanFamily family, bool outer, string? key)
    {
        if (string.IsNullOrEmpty(key)) return null;
        foreach (var m in RingCatalogFor(family, outer))
        {
            if (m.Key == key) return m;
        }
        return null;
    }

    /// <summary>The modes a family's firmware accepts, in display order, custom first.</summary>
    public static IReadOnlyList<LianLiModeInfo> CatalogFor(LianLiFanFamily family) =>
        Catalogs.TryGetValue(family, out var list) ? list : Array.Empty<LianLiModeInfo>();

    public static LianLiModeInfo? Find(LianLiFanFamily family, string? key)
    {
        if (string.IsNullOrEmpty(key)) return null;
        foreach (var m in CatalogFor(family))
        {
            if (m.Key == key) return m;
        }
        return null;
    }
}
