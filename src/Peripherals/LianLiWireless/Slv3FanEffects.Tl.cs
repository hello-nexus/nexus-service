using System;
using System.Collections.Generic;

namespace Nexus.Service.Peripherals.LianLiWireless;

public static partial class Slv3FanEffects
{
    // Every TL effect is drawn the way L-Connect draws it (Slv3FanEffects.TlLConnect.cs and
    // Slv3FanEffects.TlLConnectLibrary.cs) and takes its colour counts: one colour per fan (Static,
    // Breathing, Meteor's four passes, Stack, Twinkle), runner and ground (Runway), three blocks
    // (Color Cycle), two (Mixing, Tail Chasing, Racing, Lottery, Intertwine), up to four taken in
    // turn (the effects that step through colours) or none (Voice, Meteor Shower and Kaleidoscope
    // pick their own). Direction is offered where it changes the effect. Every TL effect can run on one half.
    private static Slv3StrimerEffectInfo[] BuildTlCatalog() => new[]
    {
        new Slv3StrimerEffectInfo { Key = "rainbow", HasSpeed = true, HasDirection = true, ColorsMin = 0, ColorsMax = 0, HasScope = true },
        new Slv3StrimerEffectInfo { Key = "rainbowMorph", HasSpeed = true, HasDirection = false, ColorsMin = 0, ColorsMax = 0, HasScope = true },
        new Slv3StrimerEffectInfo { Key = "static", HasSpeed = false, HasDirection = false, ColorsMin = 0, ColorsMax = 4, HasScope = true },
        new Slv3StrimerEffectInfo { Key = "breathing", HasSpeed = true, HasDirection = false, ColorsMin = 0, ColorsMax = 4, HasScope = true },
        new Slv3StrimerEffectInfo { Key = "runway", HasSpeed = true, HasDirection = false, ColorsMin = 0, ColorsMax = 2, HasScope = true },
        new Slv3StrimerEffectInfo { Key = "meteor", HasSpeed = true, HasDirection = true, ColorsMin = 0, ColorsMax = 4, HasScope = true },
        new Slv3StrimerEffectInfo { Key = "colorCycle", HasSpeed = true, HasDirection = true, ColorsMin = 0, ColorsMax = 3, HasScope = true },
        new Slv3StrimerEffectInfo { Key = "staggered", HasSpeed = true, HasDirection = false, ColorsMin = 0, ColorsMax = 4, HasScope = true },
        new Slv3StrimerEffectInfo { Key = "tide", HasSpeed = true, HasDirection = false, ColorsMin = 0, ColorsMax = 4, HasScope = true },
        new Slv3StrimerEffectInfo { Key = "mixing", HasSpeed = true, HasDirection = false, ColorsMin = 0, ColorsMax = 2, HasScope = true },
        new Slv3StrimerEffectInfo { Key = "voice", HasSpeed = false, HasDirection = false, ColorsMin = 0, ColorsMax = 0, HasScope = true },
        new Slv3StrimerEffectInfo { Key = "door", HasSpeed = true, HasDirection = false, ColorsMin = 0, ColorsMax = 4, HasScope = true },
        new Slv3StrimerEffectInfo { Key = "render", HasSpeed = true, HasDirection = true, ColorsMin = 0, ColorsMax = 4, HasScope = true },
        new Slv3StrimerEffectInfo { Key = "ripple", HasSpeed = true, HasDirection = false, ColorsMin = 0, ColorsMax = 4, HasScope = true },
        new Slv3StrimerEffectInfo { Key = "reflect", HasSpeed = true, HasDirection = true, ColorsMin = 0, ColorsMax = 4, HasScope = true },
        new Slv3StrimerEffectInfo { Key = "tailChasing", HasSpeed = true, HasDirection = true, ColorsMin = 0, ColorsMax = 2, HasScope = true },
        new Slv3StrimerEffectInfo { Key = "paint", HasSpeed = true, HasDirection = true, ColorsMin = 0, ColorsMax = 4, HasScope = true },
        new Slv3StrimerEffectInfo { Key = "pingPong", HasSpeed = true, HasDirection = true, ColorsMin = 0, ColorsMax = 4, HasScope = true },
        new Slv3StrimerEffectInfo { Key = "stack", HasSpeed = true, HasDirection = true, ColorsMin = 0, ColorsMax = 4, HasScope = true },
        new Slv3StrimerEffectInfo { Key = "coverCycle", HasSpeed = true, HasDirection = true, ColorsMin = 0, ColorsMax = 4, HasScope = true },
        new Slv3StrimerEffectInfo { Key = "wave", HasSpeed = true, HasDirection = true, ColorsMin = 0, ColorsMax = 4, HasScope = true },
        new Slv3StrimerEffectInfo { Key = "racing", HasSpeed = true, HasDirection = true, ColorsMin = 0, ColorsMax = 2, HasScope = true },
        new Slv3StrimerEffectInfo { Key = "lottery", HasSpeed = true, HasDirection = true, ColorsMin = 0, ColorsMax = 2, HasScope = true },
        new Slv3StrimerEffectInfo { Key = "intertwine", HasSpeed = true, HasDirection = false, ColorsMin = 0, ColorsMax = 2, HasScope = true },
        new Slv3StrimerEffectInfo { Key = "meteorShower", HasSpeed = true, HasDirection = true, ColorsMin = 0, ColorsMax = 0, HasScope = true },
        new Slv3StrimerEffectInfo { Key = "collide", HasSpeed = true, HasDirection = false, ColorsMin = 0, ColorsMax = 4, HasScope = true },
        new Slv3StrimerEffectInfo { Key = "electricCurrent", HasSpeed = true, HasDirection = false, ColorsMin = 0, ColorsMax = 4, HasScope = true },
        new Slv3StrimerEffectInfo { Key = "kaleidoscope", HasSpeed = true, HasDirection = false, ColorsMin = 0, ColorsMax = 0, HasScope = true },
        new Slv3StrimerEffectInfo { Key = "twinkle", HasSpeed = true, HasDirection = false, ColorsMin = 0, ColorsMax = 4, HasScope = true },
    };

    private static IReadOnlyDictionary<string, Func<FanEffectContext, RawAnimation>> BuildTlRenderers() =>
        new Dictionary<string, Func<FanEffectContext, RawAnimation>>(StringComparer.Ordinal)
        {
            ["rainbow"] = TlRainbow,
            ["rainbowMorph"] = TlRainbowMorph,
            ["static"] = TlStatic,
            ["breathing"] = TlBreathing,
            ["runway"] = TlRunway,
            ["meteor"] = TlMeteor,
            ["colorCycle"] = TlColorCycle,
            ["staggered"] = TlStaggered,
            ["tide"] = TlTide,
            ["mixing"] = TlMixing,
            ["voice"] = TlVoice,
            ["door"] = TlDoor,
            ["render"] = TlRender,
            ["ripple"] = TlRipple,
            ["reflect"] = TlReflect,
            ["tailChasing"] = TlTailChasing,
            ["paint"] = TlPaint,
            ["pingPong"] = TlPingPong,
            ["stack"] = TlStack,
            ["coverCycle"] = TlCoverCycle,
            ["wave"] = TlWave,
            ["racing"] = TlRacing,
            ["lottery"] = TlLottery,
            ["intertwine"] = TlIntertwine,
            ["meteorShower"] = TlMeteorShower,
            ["collide"] = TlCollide,
            ["electricCurrent"] = TlElectricCurrent,
            ["kaleidoscope"] = TlKaleidoscope,
            ["twinkle"] = TlTwinkle,
        };
}
