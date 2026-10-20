using System;

namespace Nexus.Service.Peripherals.LianLiFlex;

internal sealed partial class FlexSlInfModes
{
    internal int InfMopUp(int zone, int fans, int bright, int direction, int attach)
    {
        byte[,] strip = new byte[3, 176];
        byte[,,] zoneLedCountByFans = new byte[3, 1, 4]
        {
            { { 8, 16, 24, 32 } },
            { { 10, 20, 30, 40 } },
            { { 8, 16, 24, 32 } }
        };
        if (zone > 2)
        {
            zone = 0;
        }
        if (fans == 0 || fans > 4)
        {
            fans = 4;
        }
        if (direction > 1)
        {
            direction = 0;
        }
        if (attach > 1)
        {
            attach = 0;
        }
        attach = ((attach == 0) ? 1 : 0);
        int zoneLeds = zoneLedCountByFans[zone, 0, fans - 1];
        int frame = 0;
        for (int colorIdx = 0; colorIdx < 4; colorIdx++)
        {
            for (int head = 0; head < zoneLeds + fans + fans; head++)
            {
                for (int led = 0; led < zoneLeds; led++)
                {
                    byte red = 0;
                    byte green = 0;
                    byte blue = 0;
                    if (led <= head && led + fans + fans > head)
                    {
                        red = palette[zone, colorIdx, 0];
                        green = palette[zone, colorIdx, 1];
                        blue = palette[zone, colorIdx, 2];
                    }
                    int stripIdx = led;
                    if (colorIdx == 1 || colorIdx == 3)
                    {
                        stripIdx = zoneLeds - led - 1;
                    }
                    strip[0, stripIdx] = (byte)(red * (byte)bright >> 8);
                    strip[1, stripIdx] = (byte)(green * (byte)bright >> 8);
                    strip[2, stripIdx] = (byte)(blue * (byte)bright >> 8);
                }
                int frameOut = frame;
                for (int fan = 0; fan < fans; fan++)
                {
                    int fanBase = fan * 44;
                    int ringIdx = fan * 8;
                    if (attach == 1)
                    {
                        ringIdx = ringIdx + 8 - 1;
                    }
                    for (int led = fanBase; led < fanBase + 8; led++)
                    {
                        frames[frameOut, 0, led] = 0;
                        frames[frameOut, 1, led] = 0;
                        frames[frameOut, 2, led] = 0;
                        if (zone == 2)
                        {
                            frames[frameOut, 0, led] = strip[0, ringIdx];
                            frames[frameOut, 1, led] = strip[1, ringIdx];
                            frames[frameOut, 2, led] = strip[2, ringIdx];
                            ringIdx = ((attach != 0) ? (ringIdx - 1) : (ringIdx + 1));
                        }
                    }
                    ringIdx = fan * 10;
                    if (attach == 1)
                    {
                        ringIdx = ringIdx + 10 - 1;
                    }
                    for (int led = fanBase + 8; led < fanBase + 18; led++)
                    {
                        frames[frameOut, 0, led] = 0;
                        frames[frameOut, 1, led] = 0;
                        frames[frameOut, 2, led] = 0;
                        if (zone == 1)
                        {
                            frames[frameOut, 0, led] = strip[0, ringIdx];
                            frames[frameOut, 1, led] = strip[1, ringIdx];
                            frames[frameOut, 2, led] = strip[2, ringIdx];
                            ringIdx = ((attach != 0) ? (ringIdx - 1) : (ringIdx + 1));
                        }
                    }
                    ringIdx = fan * 8;
                    if (attach == 1)
                    {
                        ringIdx = ringIdx + 8 - 1;
                    }
                    for (int led = fanBase + 18; led < fanBase + 26; led++)
                    {
                        frames[frameOut, 0, led] = 0;
                        frames[frameOut, 1, led] = 0;
                        frames[frameOut, 2, led] = 0;
                        if (zone == 0)
                        {
                            frames[frameOut, 0, led] = strip[0, ringIdx];
                            frames[frameOut, 1, led] = strip[1, ringIdx];
                            frames[frameOut, 2, led] = strip[2, ringIdx];
                            ringIdx = ((attach != 0) ? (ringIdx - 1) : (ringIdx + 1));
                        }
                    }
                    ringIdx = fan * 10;
                    if (attach == 1)
                    {
                        ringIdx = ringIdx + 10 - 1;
                    }
                    for (int led = fanBase + 26; led < fanBase + 36; led++)
                    {
                        frames[frameOut, 0, led] = 0;
                        frames[frameOut, 1, led] = 0;
                        frames[frameOut, 2, led] = 0;
                        if (zone == 1)
                        {
                            frames[frameOut, 0, led] = strip[0, ringIdx];
                            frames[frameOut, 1, led] = strip[1, ringIdx];
                            frames[frameOut, 2, led] = strip[2, ringIdx];
                            ringIdx = ((attach != 0) ? (ringIdx - 1) : (ringIdx + 1));
                        }
                    }
                    ringIdx = fan * 8;
                    if (attach == 1)
                    {
                        ringIdx = ringIdx + 8 - 1;
                    }
                    for (int led = fanBase + 36; led < fanBase + 44; led++)
                    {
                        frames[frameOut, 0, led] = 0;
                        frames[frameOut, 1, led] = 0;
                        frames[frameOut, 2, led] = 0;
                        if (zone == 0)
                        {
                            frames[frameOut, 0, led] = strip[0, ringIdx];
                            frames[frameOut, 1, led] = strip[1, ringIdx];
                            frames[frameOut, 2, led] = strip[2, ringIdx];
                            ringIdx = ((attach != 0) ? (ringIdx - 1) : (ringIdx + 1));
                        }
                    }
                }
                frame++;
            }
        }
        return frame;
    }
    internal int InfMeteorRainbow(int zone, int fans, int bright, int direction, int attach)
    {
        byte[,] strip = new byte[3, 176];
        byte[,] zoneLedCountByFans = new byte[3, 4]
        {
            { 8, 16, 24, 32 },
            { 10, 20, 30, 40 },
            { 8, 16, 24, 32 }
        };
        byte[,] tailMask = new byte[4, 8]
        {
            { 255, 255, 0, 0, 0, 0, 0, 0 },
            { 255, 255, 255, 255, 0, 0, 0, 0 },
            { 255, 255, 255, 255, 255, 255, 0, 0 },
            { 255, 255, 255, 255, 255, 255, 255, 255 }
        };
        byte[,] rainbowReversed = new byte[3, 45];
        byte[,] rainbowRamp = new byte[3, 45]
        {
            {
                255, 128, 64, 0, 0, 0, 0, 64, 128, 255,
                128, 64, 0, 0, 0, 0, 64, 128, 255, 128,
                64, 0, 0, 0, 0, 64, 128, 255, 128, 64,
                0, 0, 0, 0, 64, 128, 255, 128, 64, 0,
                0, 0, 0, 64, 128
            },
            {
                0, 64, 128, 255, 128, 64, 0, 0, 0, 0,
                64, 128, 255, 128, 64, 0, 0, 0, 0, 64,
                128, 255, 128, 64, 0, 0, 0, 0, 64, 128,
                255, 128, 64, 0, 0, 0, 0, 64, 128, 255,
                128, 64, 0, 0, 0
            },
            {
                0, 0, 0, 0, 64, 128, 255, 128, 64, 0,
                0, 0, 0, 64, 128, 255, 128, 64, 0, 0,
                0, 0, 64, 128, 255, 128, 64, 0, 0, 0,
                0, 64, 128, 255, 128, 64, 0, 0, 0, 0,
                64, 128, 255, 128, 64
            }
        };
        if (zone > 2)
        {
            zone = 0;
        }
        if (fans == 0 || fans > 4)
        {
            fans = 4;
        }
        if (direction > 1)
        {
            direction = 0;
        }
        if (attach > 1)
        {
            attach = 0;
        }
        int zoneLeds = zoneLedCountByFans[zone, fans - 1];
        int frame = 0;
        int rampOffset = 0;
        for (int head = 0; head < zoneLeds + fans + fans - 1; head++)
        {
            int rampSrc = rampOffset;
            for (int rampIdx = 0; rampIdx < 45; rampIdx++)
            {
                int reversedIdx = 45 - rampIdx - 1;
                rainbowReversed[0, reversedIdx] = rainbowRamp[0, rampSrc];
                rainbowReversed[1, reversedIdx] = rainbowRamp[1, rampSrc];
                rainbowReversed[2, reversedIdx] = rainbowRamp[2, rampSrc];
                rampSrc++;
                if (rampSrc >= 45)
                {
                    rampSrc -= 45;
                }
            }
            rampOffset++;
            if (rampOffset >= 45)
            {
                rampOffset -= 45;
            }
            rampOffset++;
            if (rampOffset >= 45)
            {
                rampOffset -= 45;
            }
            int tailPos = 0;
            for (int led = 0; led < zoneLeds; led++)
            {
                int red = 0;
                int green = 0;
                int blue = 0;
                if (led <= head && led + fans + fans > head)
                {
                    if (tailMask[fans - 1, tailPos] != 0)
                    {
                        red = rainbowReversed[0, led];
                        green = rainbowReversed[1, led];
                        blue = rainbowReversed[2, led];
                    }
                    tailPos++;
                }
                int stripIdx = led;
                if (direction != 0)
                {
                    stripIdx = zoneLeds - led - 1;
                }
                strip[0, stripIdx] = (byte)(red * bright >> 8);
                strip[1, stripIdx] = (byte)(green * bright >> 8);
                strip[2, stripIdx] = (byte)(blue * bright >> 8);
            }
            int frameOut = frame;
            for (int fan = 0; fan < fans; fan++)
            {
                int fanBase = fan * 44;
                int ringIdx = fan * 8;
                if (attach == 0)
                {
                    ringIdx = ringIdx + 8 - 1;
                }
                for (int led = fanBase; led < fanBase + 8; led++)
                {
                    frames[frameOut, 0, led] = 0;
                    frames[frameOut, 1, led] = 0;
                    frames[frameOut, 2, led] = 0;
                    if (zone == 2)
                    {
                        frames[frameOut, 0, led] = strip[0, ringIdx];
                        frames[frameOut, 1, led] = strip[1, ringIdx];
                        frames[frameOut, 2, led] = strip[2, ringIdx];
                        ringIdx = ((attach != 1) ? (ringIdx - 1) : (ringIdx + 1));
                    }
                }
                ringIdx = fan * 10;
                if (attach == 0)
                {
                    ringIdx = ringIdx + 10 - 1;
                }
                for (int led = fanBase + 8; led < fanBase + 18; led++)
                {
                    frames[frameOut, 0, led] = 0;
                    frames[frameOut, 1, led] = 0;
                    frames[frameOut, 2, led] = 0;
                    if (zone == 1)
                    {
                        frames[frameOut, 0, led] = strip[0, ringIdx];
                        frames[frameOut, 1, led] = strip[1, ringIdx];
                        frames[frameOut, 2, led] = strip[2, ringIdx];
                        ringIdx = ((attach != 1) ? (ringIdx - 1) : (ringIdx + 1));
                    }
                }
                ringIdx = fan * 8;
                if (attach == 0)
                {
                    ringIdx = ringIdx + 8 - 1;
                }
                for (int led = fanBase + 18; led < fanBase + 26; led++)
                {
                    frames[frameOut, 0, led] = 0;
                    frames[frameOut, 1, led] = 0;
                    frames[frameOut, 2, led] = 0;
                    if (zone == 0)
                    {
                        frames[frameOut, 0, led] = strip[0, ringIdx];
                        frames[frameOut, 1, led] = strip[1, ringIdx];
                        frames[frameOut, 2, led] = strip[2, ringIdx];
                        ringIdx = ((attach != 1) ? (ringIdx - 1) : (ringIdx + 1));
                    }
                }
                ringIdx = fan * 10;
                if (attach == 0)
                {
                    ringIdx = ringIdx + 10 - 1;
                }
                for (int led = fanBase + 26; led < fanBase + 36; led++)
                {
                    frames[frameOut, 0, led] = 0;
                    frames[frameOut, 1, led] = 0;
                    frames[frameOut, 2, led] = 0;
                    if (zone == 1)
                    {
                        frames[frameOut, 0, led] = strip[0, ringIdx];
                        frames[frameOut, 1, led] = strip[1, ringIdx];
                        frames[frameOut, 2, led] = strip[2, ringIdx];
                        ringIdx = ((attach != 1) ? (ringIdx - 1) : (ringIdx + 1));
                    }
                }
                ringIdx = fan * 8;
                if (attach == 0)
                {
                    ringIdx = ringIdx + 8 - 1;
                }
                for (int led = fanBase + 36; led < fanBase + 44; led++)
                {
                    frames[frameOut, 0, led] = 0;
                    frames[frameOut, 1, led] = 0;
                    frames[frameOut, 2, led] = 0;
                    if (zone == 0)
                    {
                        frames[frameOut, 0, led] = strip[0, ringIdx];
                        frames[frameOut, 1, led] = strip[1, ringIdx];
                        frames[frameOut, 2, led] = strip[2, ringIdx];
                        ringIdx = ((attach != 1) ? (ringIdx - 1) : (ringIdx + 1));
                    }
                }
            }
            frame++;
        }
        return frame;
    }
    internal int InfWarning(int zone, int fans, int bright, int direction, int attach)
    {
        byte[,] strip = new byte[3, 176];
        byte[,] zoneLedCountByFans = new byte[3, 4]
        {
            { 8, 16, 24, 32 },
            { 10, 20, 30, 40 },
            { 8, 16, 24, 32 }
        };
        if (zone > 2)
        {
            zone = 0;
        }
        if (fans == 0 || fans > 4)
        {
            fans = 4;
        }
        if (direction > 1)
        {
            direction = 0;
        }
        if (attach > 1)
        {
            attach = 0;
        }
        byte zoneLeds = zoneLedCountByFans[zone, fans - 1];
        int frame = 0;
        for (int colorIdx = 0; colorIdx < 4; colorIdx++)
        {
            for (int blinkPhase = 0; blinkPhase < 2; blinkPhase++)
            {
                for (int repeat = 0; repeat < 8; repeat++)
                {
                    for (int led = 0; led < zoneLeds; led++)
                    {
                        strip[0, led] = 0;
                        strip[1, led] = 0;
                        strip[2, led] = 0;
                        if ((blinkPhase == 0 && zone == 2) || (blinkPhase == 1 && zone != 2))
                        {
                            strip[0, led] = (byte)(palette[zone, colorIdx, 0] * bright >> 8);
                            strip[1, led] = (byte)(palette[zone, colorIdx, 1] * bright >> 8);
                            strip[2, led] = (byte)(palette[zone, colorIdx, 2] * bright >> 8);
                        }
                    }
                    int frameOut = frame;
                    for (int fan = 0; fan < fans; fan++)
                    {
                        int fanBase = fan * 44;
                        int ringIdx = fan * 8;
                        if (attach == 0)
                        {
                            ringIdx = ringIdx + 8 - 1;
                        }
                        for (int led = fanBase; led < fanBase + 8; led++)
                        {
                            frames[frameOut, 0, led] = 0;
                            frames[frameOut, 1, led] = 0;
                            frames[frameOut, 2, led] = 0;
                            if (zone == 2)
                            {
                                frames[frameOut, 0, led] = strip[0, ringIdx];
                                frames[frameOut, 1, led] = strip[1, ringIdx];
                                frames[frameOut, 2, led] = strip[2, ringIdx];
                                ringIdx = ((attach != 1) ? (ringIdx - 1) : (ringIdx + 1));
                            }
                        }
                        ringIdx = fan * 10;
                        if (attach == 0)
                        {
                            ringIdx = ringIdx + 10 - 1;
                        }
                        for (int led = fanBase + 8; led < fanBase + 18; led++)
                        {
                            frames[frameOut, 0, led] = 0;
                            frames[frameOut, 1, led] = 0;
                            frames[frameOut, 2, led] = 0;
                            if (zone == 1)
                            {
                                frames[frameOut, 0, led] = strip[0, ringIdx];
                                frames[frameOut, 1, led] = strip[1, ringIdx];
                                frames[frameOut, 2, led] = strip[2, ringIdx];
                                ringIdx = ((attach != 1) ? (ringIdx - 1) : (ringIdx + 1));
                            }
                        }
                        ringIdx = fan * 8;
                        if (attach == 0)
                        {
                            ringIdx = ringIdx + 8 - 1;
                        }
                        for (int led = fanBase + 18; led < fanBase + 26; led++)
                        {
                            frames[frameOut, 0, led] = 0;
                            frames[frameOut, 1, led] = 0;
                            frames[frameOut, 2, led] = 0;
                            if (zone == 0)
                            {
                                frames[frameOut, 0, led] = strip[0, ringIdx];
                                frames[frameOut, 1, led] = strip[1, ringIdx];
                                frames[frameOut, 2, led] = strip[2, ringIdx];
                                ringIdx = ((attach != 1) ? (ringIdx - 1) : (ringIdx + 1));
                            }
                        }
                        ringIdx = fan * 10;
                        if (attach == 0)
                        {
                            ringIdx = ringIdx + 10 - 1;
                        }
                        for (int led = fanBase + 26; led < fanBase + 36; led++)
                        {
                            frames[frameOut, 0, led] = 0;
                            frames[frameOut, 1, led] = 0;
                            frames[frameOut, 2, led] = 0;
                            if (zone == 1)
                            {
                                frames[frameOut, 0, led] = strip[0, ringIdx];
                                frames[frameOut, 1, led] = strip[1, ringIdx];
                                frames[frameOut, 2, led] = strip[2, ringIdx];
                                ringIdx = ((attach != 1) ? (ringIdx - 1) : (ringIdx + 1));
                            }
                        }
                        ringIdx = fan * 8;
                        if (attach == 0)
                        {
                            ringIdx = ringIdx + 8 - 1;
                        }
                        for (int led = fanBase + 36; led < fanBase + 44; led++)
                        {
                            frames[frameOut, 0, led] = 0;
                            frames[frameOut, 1, led] = 0;
                            frames[frameOut, 2, led] = 0;
                            if (zone == 0)
                            {
                                frames[frameOut, 0, led] = strip[0, ringIdx];
                                frames[frameOut, 1, led] = strip[1, ringIdx];
                                frames[frameOut, 2, led] = strip[2, ringIdx];
                                ringIdx = ((attach != 1) ? (ringIdx - 1) : (ringIdx + 1));
                            }
                        }
                    }
                    frame++;
                }
            }
        }
        return frame;
    }
    internal int InfVoice(int zone, int fans, int bright, int direction, int attach)
    {
        byte[,] strip = new byte[3, 176];
        byte[] centerOrderDefault = new byte[32]
        {
            3, 4, 2, 5, 1, 6, 8, 7, 11, 12,
            10, 13, 9, 14, 16, 15, 19, 20, 18, 21,
            17, 22, 24, 23, 27, 28, 26, 29, 25, 30,
            32, 31
        };
        byte[] centerOrderRightAttach = new byte[32]
        {
            7, 8, 6, 1, 5, 2, 4, 3, 15, 16,
            14, 9, 13, 10, 12, 11, 23, 24, 22, 17,
            21, 18, 20, 19, 31, 32, 30, 25, 29, 26,
            28, 27
        };
        byte[,] zoneLedCountByFans = new byte[3, 4]
        {
            { 8, 16, 24, 32 },
            { 10, 20, 30, 40 },
            { 8, 16, 24, 32 }
        };
        if (zone > 2)
        {
            zone = 0;
        }
        if (fans == 0 || fans > 4)
        {
            fans = 4;
        }
        if (direction > 1)
        {
            direction = 0;
        }
        if (attach > 1)
        {
            attach = 0;
        }
        byte zoneLeds = zoneLedCountByFans[zone, fans - 1];
        int frame = 0;
        for (int colorIdx = 0; colorIdx < 4; colorIdx++)
        {
            for (int band = 0; band < 3; band++)
            {
                for (int half = 0; half < 2; half++)
                {
                    int levelMax = band switch
                    {
                        0 => zoneLeds,
                        1 => zoneLeds * 2 / 3,
                        _ => zoneLeds / 3,
                    };
                    for (int level = 0; level < levelMax; level++)
                    {
                        for (int led = 0; led < zoneLeds; led++)
                        {
                            int stripIdx = led;
                            if (direction == 1)
                            {
                                stripIdx = zoneLeds - led - 1;
                            }
                            if (zone == 2 && attach == 0)
                            {
                                stripIdx = centerOrderDefault[stripIdx] - 1;
                            }
                            if (zone == 2 && attach == 1)
                            {
                                stripIdx = centerOrderRightAttach[stripIdx] - 1;
                            }
                            strip[0, stripIdx] = (byte)(palette[zone, colorIdx, 0] * bright >> 8);
                            strip[1, stripIdx] = (byte)(palette[zone, colorIdx, 1] * bright >> 8);
                            strip[2, stripIdx] = (byte)(palette[zone, colorIdx, 2] * bright >> 8);
                            if (led > level && half == 0)
                            {
                                strip[0, stripIdx] /= 16;
                                strip[1, stripIdx] /= 16;
                                strip[2, stripIdx] /= 16;
                            }
                            if (led > levelMax - level && half == 1)
                            {
                                strip[0, stripIdx] /= 16;
                                strip[1, stripIdx] /= 16;
                                strip[2, stripIdx] /= 16;
                            }
                        }
                        int frameOut = frame;
                        for (int fan = 0; fan < fans; fan++)
                        {
                            int fanBase = fan * 44;
                            int ringIdx = fan * 8;
                            for (int led = fanBase; led < fanBase + 8; led++)
                            {
                                frames[frameOut, 0, led] = 0;
                                frames[frameOut, 1, led] = 0;
                                frames[frameOut, 2, led] = 0;
                                if (zone == 2)
                                {
                                    frames[frameOut, 0, led] = strip[0, ringIdx];
                                    frames[frameOut, 1, led] = strip[1, ringIdx];
                                    frames[frameOut, 2, led] = strip[2, ringIdx];
                                    ringIdx++;
                                }
                            }
                            ringIdx = fan * 10;
                            if (attach == 0)
                            {
                                ringIdx = ringIdx + 10 - 1;
                            }
                            for (int led = fanBase + 8; led < fanBase + 18; led++)
                            {
                                frames[frameOut, 0, led] = 0;
                                frames[frameOut, 1, led] = 0;
                                frames[frameOut, 2, led] = 0;
                                if (zone == 1)
                                {
                                    frames[frameOut, 0, led] = strip[0, ringIdx];
                                    frames[frameOut, 1, led] = strip[1, ringIdx];
                                    frames[frameOut, 2, led] = strip[2, ringIdx];
                                    ringIdx = ((attach != 1) ? (ringIdx - 1) : (ringIdx + 1));
                                }
                            }
                            ringIdx = fan * 8;
                            if (attach == 0)
                            {
                                ringIdx = ringIdx + 8 - 1;
                            }
                            for (int led = fanBase + 18; led < fanBase + 26; led++)
                            {
                                frames[frameOut, 0, led] = 0;
                                frames[frameOut, 1, led] = 0;
                                frames[frameOut, 2, led] = 0;
                                if (zone == 0)
                                {
                                    frames[frameOut, 0, led] = strip[0, ringIdx];
                                    frames[frameOut, 1, led] = strip[1, ringIdx];
                                    frames[frameOut, 2, led] = strip[2, ringIdx];
                                    ringIdx = ((attach != 1) ? (ringIdx - 1) : (ringIdx + 1));
                                }
                            }
                            ringIdx = fan * 10;
                            if (attach == 0)
                            {
                                ringIdx = ringIdx + 10 - 1;
                            }
                            for (int led = fanBase + 26; led < fanBase + 36; led++)
                            {
                                frames[frameOut, 0, led] = 0;
                                frames[frameOut, 1, led] = 0;
                                frames[frameOut, 2, led] = 0;
                                if (zone == 1)
                                {
                                    frames[frameOut, 0, led] = strip[0, ringIdx];
                                    frames[frameOut, 1, led] = strip[1, ringIdx];
                                    frames[frameOut, 2, led] = strip[2, ringIdx];
                                    ringIdx = ((attach != 1) ? (ringIdx - 1) : (ringIdx + 1));
                                }
                            }
                            ringIdx = fan * 8;
                            if (attach == 0)
                            {
                                ringIdx = ringIdx + 8 - 1;
                            }
                            for (int led = fanBase + 36; led < fanBase + 44; led++)
                            {
                                frames[frameOut, 0, led] = 0;
                                frames[frameOut, 1, led] = 0;
                                frames[frameOut, 2, led] = 0;
                                if (zone == 0)
                                {
                                    frames[frameOut, 0, led] = strip[0, ringIdx];
                                    frames[frameOut, 1, led] = strip[1, ringIdx];
                                    frames[frameOut, 2, led] = strip[2, ringIdx];
                                    ringIdx = ((attach != 1) ? (ringIdx - 1) : (ringIdx + 1));
                                }
                            }
                        }
                        frame++;
                    }
                }
            }
        }
        int outFrames = frame / 2;
        byte[,,] halved = new byte[outFrames, 3, ledCount];
        for (int frameIdx = 0; frameIdx < outFrames; frameIdx++)
        {
            for (int channel = 0; channel < 3; channel++)
            {
                for (int ledIdx = 0; ledIdx < ledCount; ledIdx++)
                {
                    halved[frameIdx, channel, ledIdx] = frames[frameIdx * 2, channel, ledIdx];
                }
            }
        }
        frames = halved;
        return outFrames;
    }
    internal int InfMixing(int zone, int fans, int bright, int direction, int attach)
    {
        byte[,] strip = new byte[3, 176];
        byte[,] mixedColors = new byte[3, 3];
        byte[,] zoneLedCountByFans = new byte[3, 4]
        {
            { 8, 16, 24, 32 },
            { 10, 20, 30, 40 },
            { 8, 16, 24, 32 }
        };
        if (zone > 2)
        {
            zone = 0;
        }
        if (fans == 0 || fans > 4)
        {
            fans = 4;
        }
        if (direction > 1)
        {
            direction = 0;
        }
        if (attach > 1)
        {
            attach = 0;
        }
        byte halfLeds = (byte)(zoneLedCountByFans[zone, fans - 1] / 2);
        for (int row = 0; row < 3; row++)
        {
            for (int channel = 0; channel < 3; channel++)
            {
                if (palette[row, 0, channel] + palette[row, 1, channel] >= 255)
                {
                    mixedColors[row, channel] = byte.MaxValue;
                }
                else
                {
                    mixedColors[row, channel] = (byte)(palette[row, 0, channel] + palette[row, 1, channel]);
                }
            }
        }
        for (int row = 0; row < 3; row++)
        {
            if (mixedColors[row, 0] + mixedColors[row, 1] + mixedColors[row, 2] > PowerCap)
            {
                var (cappedRed, cappedGreen, cappedBlue) = GetRgb(mixedColors[row, 0], mixedColors[row, 1], mixedColors[row, 2]);
                mixedColors[row, 0] = cappedRed;
                mixedColors[row, 1] = cappedGreen;
                mixedColors[row, 2] = cappedBlue;
            }
        }
        int frame = 0;
        for (int pass = 0; pass < 3; pass++)
        {
            for (int head = 0; head < halfLeds + fans + fans - 1; head++)
            {
                for (int led = 0; led < halfLeds; led++)
                {
                    byte red = 0;
                    byte green = 0;
                    byte blue = 0;
                    if ((pass == 1 && led < head) || (pass == 2 && led + fans + fans > head))
                    {
                        red = mixedColors[zone, 0];
                        green = mixedColors[zone, 1];
                        blue = mixedColors[zone, 2];
                    }
                    else if (led < head && led + fans + fans > head)
                    {
                        red = palette[zone, 0, 0];
                        green = palette[zone, 0, 1];
                        blue = palette[zone, 0, 2];
                    }
                    int stripIdx = led;
                    if (pass != 0)
                    {
                        stripIdx = halfLeds - led - 1;
                    }
                    strip[0, stripIdx] = (byte)(red * bright >> 8);
                    strip[1, stripIdx] = (byte)(green * bright >> 8);
                    strip[2, stripIdx] = (byte)(blue * bright >> 8);
                }
                for (int led = 0; led < halfLeds; led++)
                {
                    byte red = 0;
                    byte green = 0;
                    byte blue = 0;
                    if ((pass == 1 && led < head) || (pass == 2 && led + fans + fans > head))
                    {
                        red = mixedColors[zone, 0];
                        green = mixedColors[zone, 1];
                        blue = mixedColors[zone, 2];
                    }
                    else if (led < head && led + fans + fans > head)
                    {
                        red = palette[zone, 1, 0];
                        green = palette[zone, 1, 1];
                        blue = palette[zone, 1, 2];
                    }
                    int stripIdx = halfLeds - led - 1;
                    if (pass != 0)
                    {
                        stripIdx = led;
                    }
                    strip[0, stripIdx + halfLeds] = (byte)(red * bright >> 8);
                    strip[1, stripIdx + halfLeds] = (byte)(green * bright >> 8);
                    strip[2, stripIdx + halfLeds] = (byte)(blue * bright >> 8);
                }
                if (pass != 0)
                {
                    head++;
                }
                int frameOut = frame;
                for (int fan = 0; fan < fans; fan++)
                {
                    int fanBase = fan * 44;
                    int ringIdx = fan * 8;
                    for (int led = fanBase; led < fanBase + 8; led++)
                    {
                        frames[frameOut, 0, led] = 0;
                        frames[frameOut, 1, led] = 0;
                        frames[frameOut, 2, led] = 0;
                        if (zone == 2)
                        {
                            frames[frameOut, 0, led] = strip[0, ringIdx];
                            frames[frameOut, 1, led] = strip[1, ringIdx];
                            frames[frameOut, 2, led] = strip[2, ringIdx];
                            ringIdx++;
                        }
                    }
                    ringIdx = fan * 10;
                    if (attach == 0)
                    {
                        ringIdx = ringIdx + 10 - 1;
                    }
                    for (int led = fanBase + 8; led < fanBase + 18; led++)
                    {
                        frames[frameOut, 0, led] = 0;
                        frames[frameOut, 1, led] = 0;
                        frames[frameOut, 2, led] = 0;
                        if (zone == 1)
                        {
                            frames[frameOut, 0, led] = strip[0, ringIdx];
                            frames[frameOut, 1, led] = strip[1, ringIdx];
                            frames[frameOut, 2, led] = strip[2, ringIdx];
                            ringIdx = ((attach != 1) ? (ringIdx - 1) : (ringIdx + 1));
                        }
                    }
                    ringIdx = fan * 8;
                    if (attach == 0)
                    {
                        ringIdx = ringIdx + 8 - 1;
                    }
                    for (int led = fanBase + 18; led < fanBase + 26; led++)
                    {
                        frames[frameOut, 0, led] = 0;
                        frames[frameOut, 1, led] = 0;
                        frames[frameOut, 2, led] = 0;
                        if (zone == 0)
                        {
                            frames[frameOut, 0, led] = strip[0, ringIdx];
                            frames[frameOut, 1, led] = strip[1, ringIdx];
                            frames[frameOut, 2, led] = strip[2, ringIdx];
                            ringIdx = ((attach != 1) ? (ringIdx - 1) : (ringIdx + 1));
                        }
                    }
                    ringIdx = fan * 10;
                    if (attach == 0)
                    {
                        ringIdx = ringIdx + 10 - 1;
                    }
                    for (int led = fanBase + 26; led < fanBase + 36; led++)
                    {
                        frames[frameOut, 0, led] = 0;
                        frames[frameOut, 1, led] = 0;
                        frames[frameOut, 2, led] = 0;
                        if (zone == 1)
                        {
                            frames[frameOut, 0, led] = strip[0, ringIdx];
                            frames[frameOut, 1, led] = strip[1, ringIdx];
                            frames[frameOut, 2, led] = strip[2, ringIdx];
                            ringIdx = ((attach != 1) ? (ringIdx - 1) : (ringIdx + 1));
                        }
                    }
                    ringIdx = fan * 8;
                    if (attach == 0)
                    {
                        ringIdx = ringIdx + 8 - 1;
                    }
                    for (int led = fanBase + 36; led < fanBase + 44; led++)
                    {
                        frames[frameOut, 0, led] = 0;
                        frames[frameOut, 1, led] = 0;
                        frames[frameOut, 2, led] = 0;
                        if (zone == 0)
                        {
                            frames[frameOut, 0, led] = strip[0, ringIdx];
                            frames[frameOut, 1, led] = strip[1, ringIdx];
                            frames[frameOut, 2, led] = strip[2, ringIdx];
                            ringIdx = ((attach != 1) ? (ringIdx - 1) : (ringIdx + 1));
                        }
                    }
                }
                frame++;
            }
        }
        return frame;
    }
    internal int InfTide(int zone, int fans, int bright, int direction, int attach)
    {
        byte[,] strip = new byte[3, 176];
        byte[] centerOrderDefault = new byte[32]
        {
            3, 4, 2, 5, 1, 6, 8, 7, 11, 12,
            10, 13, 9, 14, 16, 15, 19, 20, 18, 21,
            17, 22, 24, 23, 27, 28, 26, 29, 25, 30,
            32, 31
        };
        byte[] centerOrderRightAttach = new byte[32]
        {
            7, 8, 6, 1, 5, 2, 4, 3, 15, 16,
            14, 9, 13, 10, 12, 11, 23, 24, 22, 17,
            21, 18, 20, 19, 31, 32, 30, 25, 29, 26,
            28, 27
        };
        byte[,] zoneLedCountByFans = new byte[3, 4]
        {
            { 8, 16, 24, 32 },
            { 10, 20, 30, 40 },
            { 8, 16, 24, 32 }
        };
        if (zone > 2)
        {
            zone = 0;
        }
        if (fans == 0 || fans > 4)
        {
            fans = 4;
        }
        if (direction > 1)
        {
            direction = 0;
        }
        if (attach > 1)
        {
            attach = 0;
        }
        byte halfLeds = (byte)(zoneLedCountByFans[zone, fans - 1] / 2);
        int frame = 0;
        for (int colorIdx = 0; colorIdx < 4; colorIdx++)
        {
            for (int head = 0; head < halfLeds + fans + fans - 1; head++)
            {
                for (int led = 0; led < halfLeds; led++)
                {
                    int paletteRow = ((colorIdx != 0) ? (colorIdx - 1) : 3);
                    if (led <= head)
                    {
                        paletteRow = colorIdx;
                    }
                    byte red = palette[zone, paletteRow, 0];
                    byte green = palette[zone, paletteRow, 1];
                    byte blue = palette[zone, paletteRow, 2];
                    int stripIdx = led;
                    if (zone == 2 && attach == 0)
                    {
                        stripIdx = centerOrderDefault[stripIdx] - 1;
                    }
                    if (zone == 2 && attach == 1)
                    {
                        stripIdx = centerOrderRightAttach[stripIdx] - 1;
                    }
                    strip[0, stripIdx] = (byte)(red * bright >> 8);
                    strip[1, stripIdx] = (byte)(green * bright >> 8);
                    strip[2, stripIdx] = (byte)(blue * bright >> 8);
                }
                for (int led = 0; led < halfLeds; led++)
                {
                    int paletteRow = ((colorIdx != 0) ? (colorIdx - 1) : 3);
                    if (led <= head)
                    {
                        paletteRow = colorIdx;
                    }
                    byte red = palette[zone, paletteRow, 0];
                    byte green = palette[zone, paletteRow, 1];
                    byte blue = palette[zone, paletteRow, 2];
                    int stripIdx = (byte)(halfLeds - led - 1 + halfLeds);
                    if (zone == 2 && attach == 0)
                    {
                        stripIdx = centerOrderDefault[stripIdx] - 1;
                    }
                    if (zone == 2 && attach == 1)
                    {
                        stripIdx = centerOrderRightAttach[stripIdx] - 1;
                    }
                    strip[0, stripIdx] = (byte)(red * bright >> 8);
                    strip[1, stripIdx] = (byte)(green * bright >> 8);
                    strip[2, stripIdx] = (byte)(blue * bright >> 8);
                }
                int frameOut = frame;
                for (int fan = 0; fan < fans; fan++)
                {
                    int fanBase = fan * 44;
                    int ringIdx = fan * 8;
                    for (int led = fanBase; led < fanBase + 8; led++)
                    {
                        frames[frameOut, 0, led] = 0;
                        frames[frameOut, 1, led] = 0;
                        frames[frameOut, 2, led] = 0;
                        if (zone == 2)
                        {
                            frames[frameOut, 0, led] = strip[0, ringIdx];
                            frames[frameOut, 1, led] = strip[1, ringIdx];
                            frames[frameOut, 2, led] = strip[2, ringIdx];
                            ringIdx++;
                        }
                    }
                    ringIdx = fan * 10;
                    if (attach == 0)
                    {
                        ringIdx = ringIdx + 10 - 1;
                    }
                    for (int led = fanBase + 8; led < fanBase + 18; led++)
                    {
                        frames[frameOut, 0, led] = 0;
                        frames[frameOut, 1, led] = 0;
                        frames[frameOut, 2, led] = 0;
                        if (zone == 1)
                        {
                            frames[frameOut, 0, led] = strip[0, ringIdx];
                            frames[frameOut, 1, led] = strip[1, ringIdx];
                            frames[frameOut, 2, led] = strip[2, ringIdx];
                            ringIdx = ((attach != 1) ? (ringIdx - 1) : (ringIdx + 1));
                        }
                    }
                    ringIdx = fan * 8;
                    if (attach == 0)
                    {
                        ringIdx = ringIdx + 8 - 1;
                    }
                    for (int led = fanBase + 18; led < fanBase + 26; led++)
                    {
                        frames[frameOut, 0, led] = 0;
                        frames[frameOut, 1, led] = 0;
                        frames[frameOut, 2, led] = 0;
                        if (zone == 0)
                        {
                            frames[frameOut, 0, led] = strip[0, ringIdx];
                            frames[frameOut, 1, led] = strip[1, ringIdx];
                            frames[frameOut, 2, led] = strip[2, ringIdx];
                            ringIdx = ((attach != 1) ? (ringIdx - 1) : (ringIdx + 1));
                        }
                    }
                    ringIdx = fan * 10;
                    if (attach == 0)
                    {
                        ringIdx = ringIdx + 10 - 1;
                    }
                    for (int led = fanBase + 26; led < fanBase + 36; led++)
                    {
                        frames[frameOut, 0, led] = 0;
                        frames[frameOut, 1, led] = 0;
                        frames[frameOut, 2, led] = 0;
                        if (zone == 1)
                        {
                            frames[frameOut, 0, led] = strip[0, ringIdx];
                            frames[frameOut, 1, led] = strip[1, ringIdx];
                            frames[frameOut, 2, led] = strip[2, ringIdx];
                            ringIdx = ((attach != 1) ? (ringIdx - 1) : (ringIdx + 1));
                        }
                    }
                    ringIdx = fan * 8;
                    if (attach == 0)
                    {
                        ringIdx = ringIdx + 8 - 1;
                    }
                    for (int led = fanBase + 36; led < fanBase + 44; led++)
                    {
                        frames[frameOut, 0, led] = 0;
                        frames[frameOut, 1, led] = 0;
                        frames[frameOut, 2, led] = 0;
                        if (zone == 0)
                        {
                            frames[frameOut, 0, led] = strip[0, ringIdx];
                            frames[frameOut, 1, led] = strip[1, ringIdx];
                            frames[frameOut, 2, led] = strip[2, ringIdx];
                            ringIdx = ((attach != 1) ? (ringIdx - 1) : (ringIdx + 1));
                        }
                    }
                }
                frame++;
            }
        }
        return frame;
    }
    internal int InfScan(int zone, int fans, int bright, int direction, int attach)
    {
        byte[,] strip = new byte[3, 176];
        byte[,] zoneLedCountByFans = new byte[3, 4]
        {
            { 8, 16, 24, 32 },
            { 10, 20, 30, 40 },
            { 8, 16, 24, 32 }
        };
        byte[] centerOrderDefault = new byte[32]
        {
            3, 4, 2, 5, 1, 6, 8, 7, 11, 12,
            10, 13, 9, 14, 16, 15, 19, 20, 18, 21,
            17, 22, 24, 23, 27, 28, 26, 29, 25, 30,
            32, 31
        };
        byte[] centerOrderRightAttach = new byte[32]
        {
            7, 8, 6, 1, 5, 2, 4, 3, 15, 16,
            14, 9, 13, 10, 12, 11, 23, 24, 22, 17,
            21, 18, 20, 19, 31, 32, 30, 25, 29, 26,
            28, 27
        };
        if (zone > 2)
        {
            zone = 0;
        }
        if (fans == 0 || fans > 4)
        {
            fans = 4;
        }
        if (direction > 1)
        {
            direction = 0;
        }
        if (attach > 1)
        {
            attach = 0;
        }
        byte zoneLeds = zoneLedCountByFans[zone, fans - 1];
        int frame = 0;
        for (int pass = 0; pass < 2; pass++)
        {
            for (int head = 0; head < zoneLeds + fans + fans; head++)
            {
                for (int led = 0; led < zoneLeds; led++)
                {
                    byte red = 0;
                    byte green = 0;
                    byte blue = 0;
                    if (led <= head && led + fans > head)
                    {
                        red = palette[zone, 0, 0];
                        green = palette[zone, 0, 1];
                        blue = palette[zone, 0, 2];
                    }
                    int stripIdx = led;
                    if (pass == 1)
                    {
                        stripIdx = zoneLeds - led - 1;
                    }
                    if (zone == 2 && attach == 0)
                    {
                        stripIdx = centerOrderDefault[led] - 1;
                        if (pass == 1)
                        {
                            stripIdx = centerOrderDefault[zoneLeds - led - 1] - 1;
                        }
                    }
                    if (zone == 2 && attach == 1)
                    {
                        stripIdx = centerOrderRightAttach[led] - 1;
                        if (pass == 1)
                        {
                            stripIdx = centerOrderRightAttach[zoneLeds - led - 1] - 1;
                        }
                    }
                    strip[0, stripIdx] = (byte)(red * bright >> 8);
                    strip[1, stripIdx] = (byte)(green * bright >> 8);
                    strip[2, stripIdx] = (byte)(blue * bright >> 8);
                }
                int frameOut = frame;
                for (int fan = 0; fan < fans; fan++)
                {
                    int fanBase = fan * 44;
                    int ringIdx = fan * 8;
                    for (int led = fanBase; led < fanBase + 8; led++)
                    {
                        frames[frameOut, 0, led] = 0;
                        frames[frameOut, 1, led] = 0;
                        frames[frameOut, 2, led] = 0;
                        if (zone == 2)
                        {
                            frames[frameOut, 0, led] = strip[0, ringIdx];
                            frames[frameOut, 1, led] = strip[1, ringIdx];
                            frames[frameOut, 2, led] = strip[2, ringIdx];
                            ringIdx++;
                        }
                    }
                    ringIdx = fan * 10;
                    if (attach == 0)
                    {
                        ringIdx = ringIdx + 10 - 1;
                    }
                    for (int led = fanBase + 8; led < fanBase + 18; led++)
                    {
                        frames[frameOut, 0, led] = 0;
                        frames[frameOut, 1, led] = 0;
                        frames[frameOut, 2, led] = 0;
                        if (zone == 1)
                        {
                            frames[frameOut, 0, led] = strip[0, ringIdx];
                            frames[frameOut, 1, led] = strip[1, ringIdx];
                            frames[frameOut, 2, led] = strip[2, ringIdx];
                            ringIdx = ((attach != 1) ? (ringIdx - 1) : (ringIdx + 1));
                        }
                    }
                    ringIdx = fan * 8;
                    if (attach == 0)
                    {
                        ringIdx = ringIdx + 8 - 1;
                    }
                    for (int led = fanBase + 18; led < fanBase + 26; led++)
                    {
                        frames[frameOut, 0, led] = 0;
                        frames[frameOut, 1, led] = 0;
                        frames[frameOut, 2, led] = 0;
                        if (zone == 0)
                        {
                            frames[frameOut, 0, led] = strip[0, ringIdx];
                            frames[frameOut, 1, led] = strip[1, ringIdx];
                            frames[frameOut, 2, led] = strip[2, ringIdx];
                            ringIdx = ((attach != 1) ? (ringIdx - 1) : (ringIdx + 1));
                        }
                    }
                    ringIdx = fan * 10;
                    if (attach == 0)
                    {
                        ringIdx = ringIdx + 10 - 1;
                    }
                    for (int led = fanBase + 26; led < fanBase + 36; led++)
                    {
                        frames[frameOut, 0, led] = 0;
                        frames[frameOut, 1, led] = 0;
                        frames[frameOut, 2, led] = 0;
                        if (zone == 1)
                        {
                            frames[frameOut, 0, led] = strip[0, ringIdx];
                            frames[frameOut, 1, led] = strip[1, ringIdx];
                            frames[frameOut, 2, led] = strip[2, ringIdx];
                            ringIdx = ((attach != 1) ? (ringIdx - 1) : (ringIdx + 1));
                        }
                    }
                    ringIdx = fan * 8;
                    if (attach == 0)
                    {
                        ringIdx = ringIdx + 8 - 1;
                    }
                    for (int led = fanBase + 36; led < fanBase + 44; led++)
                    {
                        frames[frameOut, 0, led] = 0;
                        frames[frameOut, 1, led] = 0;
                        frames[frameOut, 2, led] = 0;
                        if (zone == 0)
                        {
                            frames[frameOut, 0, led] = strip[0, ringIdx];
                            frames[frameOut, 1, led] = strip[1, ringIdx];
                            frames[frameOut, 2, led] = strip[2, ringIdx];
                            ringIdx = ((attach != 1) ? (ringIdx - 1) : (ringIdx + 1));
                        }
                    }
                }
                frame++;
            }
        }
        return frame;
    }
}
