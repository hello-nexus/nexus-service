using System;

namespace Nexus.Service.Peripherals.LianLiFlex;

internal sealed partial class FlexSlInfModes
{
    private byte[,,] frames;
    private byte[,,] palette;
    private readonly int ledCount;

    public FlexSlInfModes(int fans, byte[,,] palette)
    {
        this.palette = palette;
        ledCount = fans * 44;
        frames = new byte[1024, 3, ledCount];
    }

    public byte[,,] Frames => frames;

    private void mapUserColor()
    {
        var mapped = new byte[4, 4, 3];
        CopyColor(mapped, 2, 0, 1);
        CopyColor(mapped, 1, 0, 1);
        CopyColor(mapped, 0, 0, 0);
        CopyColor(mapped, 2, 1, 0);
        CopyColor(mapped, 1, 1, 0);
        CopyColor(mapped, 0, 1, 1);
        CopyColor(mapped, 2, 2, 3);
        CopyColor(mapped, 1, 2, 3);
        CopyColor(mapped, 0, 2, 2);
        CopyColor(mapped, 2, 3, 2);
        CopyColor(mapped, 1, 3, 2);
        CopyColor(mapped, 0, 3, 3);
        palette = mapped;
    }

    private void CopyColor(byte[,,] colors, int zone, int slot, int srcSlot)
    {
        for (var channel = 0; channel < 3; channel++)
        {
            colors[zone, slot, channel] = palette[0, srcSlot, channel];
        }
    }

    private const int PowerCap = FlexPalette.PowerCap;

    private static (byte, byte, byte) GetRgb(byte r, byte g, byte b) => FlexPalette.Cap(r, g, b);

    internal int InfRainbow(int zone, int fans, int bright, int direction, int attach)
    {
        byte[,] strip = new byte[3, 176];
        byte[] rampStepByFans = new byte[4] { 12, 6, 4, 3 };
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
        byte[,] innerRamp = new byte[3, 120]
        {
            {
                255, 248, 242, 235, 229, 222, 216, 209, 203, 196,
                190, 183, 177, 170, 164, 157, 151, 144, 138, 131,
                125, 118, 112, 105, 99, 92, 86, 79, 73, 66,
                60, 53, 47, 40, 34, 27, 21, 14, 8, 2,
                0, 0, 0, 0, 0, 0, 0, 0, 0, 0,
                0, 0, 0, 0, 0, 0, 0, 0, 0, 0,
                0, 0, 0, 0, 0, 0, 0, 0, 0, 0,
                0, 0, 0, 0, 0, 0, 0, 0, 0, 0,
                0, 2, 8, 14, 21, 27, 34, 40, 47, 53,
                60, 66, 73, 79, 86, 92, 99, 105, 112, 118,
                125, 131, 138, 144, 151, 157, 164, 170, 177, 183,
                190, 196, 203, 209, 216, 222, 229, 235, 242, 248
            },
            {
                0, 2, 8, 14, 21, 27, 34, 40, 47, 53,
                60, 66, 73, 79, 86, 92, 99, 105, 112, 118,
                125, 131, 138, 144, 151, 157, 164, 170, 177, 183,
                190, 196, 203, 209, 216, 222, 229, 235, 242, 248,
                255, 248, 242, 235, 229, 222, 216, 209, 203, 196,
                190, 183, 177, 170, 164, 157, 151, 144, 138, 131,
                125, 118, 112, 105, 99, 92, 86, 79, 73, 66,
                60, 53, 47, 40, 34, 27, 21, 14, 8, 2,
                0, 0, 0, 0, 0, 0, 0, 0, 0, 0,
                0, 0, 0, 0, 0, 0, 0, 0, 0, 0,
                0, 0, 0, 0, 0, 0, 0, 0, 0, 0,
                0, 0, 0, 0, 0, 0, 0, 0, 0, 0
            },
            {
                0, 0, 0, 0, 0, 0, 0, 0, 0, 0,
                0, 0, 0, 0, 0, 0, 0, 0, 0, 0,
                0, 0, 0, 0, 0, 0, 0, 0, 0, 0,
                0, 0, 0, 0, 0, 0, 0, 0, 0, 0,
                0, 2, 8, 14, 21, 27, 34, 40, 47, 53,
                60, 66, 73, 79, 86, 92, 99, 105, 112, 118,
                125, 131, 138, 144, 151, 157, 164, 170, 177, 183,
                190, 196, 203, 209, 216, 222, 229, 235, 242, 248,
                255, 248, 242, 235, 229, 222, 216, 209, 203, 196,
                190, 183, 177, 170, 164, 157, 151, 144, 138, 131,
                125, 118, 112, 105, 99, 92, 86, 79, 73, 66,
                60, 53, 47, 40, 34, 27, 21, 14, 8, 2
            }
        };
        byte[,] outerRamp = new byte[3, 96]
        {
            {
                255, 248, 240, 232, 224, 216, 208, 200, 192, 184,
                176, 168, 160, 152, 144, 136, 128, 120, 112, 104,
                96, 88, 80, 72, 64, 56, 48, 40, 32, 24,
                16, 8, 0, 0, 0, 0, 0, 0, 0, 0,
                0, 0, 0, 0, 0, 0, 0, 0, 0, 0,
                0, 0, 0, 0, 0, 0, 0, 0, 0, 0,
                0, 0, 0, 0, 0, 8, 16, 24, 32, 40,
                48, 56, 64, 72, 80, 88, 96, 104, 112, 120,
                128, 136, 144, 152, 160, 168, 176, 184, 192, 200,
                208, 216, 224, 232, 240, 248
            },
            {
                0, 8, 16, 24, 32, 40, 48, 56, 64, 72,
                80, 88, 96, 104, 112, 120, 128, 136, 144, 152,
                160, 168, 176, 184, 192, 200, 208, 216, 224, 232,
                240, 248, 255, 248, 240, 232, 224, 216, 208, 200,
                192, 184, 176, 168, 160, 152, 144, 136, 128, 120,
                112, 104, 96, 88, 80, 72, 64, 56, 48, 40,
                32, 24, 16, 8, 0, 0, 0, 0, 0, 0,
                0, 0, 0, 0, 0, 0, 0, 0, 0, 0,
                0, 0, 0, 0, 0, 0, 0, 0, 0, 0,
                0, 0, 0, 0, 0, 0
            },
            {
                0, 0, 0, 0, 0, 0, 0, 0, 0, 0,
                0, 0, 0, 0, 0, 0, 0, 0, 0, 0,
                0, 0, 0, 0, 0, 0, 0, 0, 0, 0,
                0, 0, 0, 8, 16, 24, 32, 40, 48, 56,
                64, 72, 80, 88, 96, 104, 112, 120, 128, 136,
                144, 152, 160, 168, 176, 184, 192, 200, 208, 216,
                224, 232, 240, 248, 255, 248, 240, 232, 224, 216,
                208, 200, 192, 184, 176, 168, 160, 152, 144, 136,
                128, 120, 112, 104, 96, 88, 80, 72, 64, 56,
                48, 40, 32, 24, 16, 8
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
        byte zoneLeds = zoneLedCountByFans[zone, fans - 1];
        byte rampStep = rampStepByFans[fans - 1];
        int frame = 0;
        for (int shift = 0; shift < zoneLeds; shift++)
        {
            int rampPos = shift * rampStep;
            for (int led = 0; led < zoneLeds; led++)
            {
                int ledSlot = led;
                if (direction == 0)
                {
                    ledSlot = zoneLeds - led - 1;
                }
                switch (zone)
                {
                case 1:
                    strip[0, ledSlot] = (byte)(innerRamp[0, rampPos] * bright >> 8);
                    strip[1, ledSlot] = (byte)(innerRamp[1, rampPos] * bright >> 8);
                    strip[2, ledSlot] = (byte)(innerRamp[2, rampPos] * bright >> 8);
                    rampPos += rampStep;
                    if (rampPos >= 120)
                    {
                        rampPos -= 120;
                    }
                    continue;
                case 2:
                    if (attach == 0)
                    {
                        ledSlot = centerOrderDefault[ledSlot] - 1;
                    }
                    break;
                }
                if (zone == 2 && attach == 1)
                {
                    ledSlot = centerOrderRightAttach[ledSlot] - 1;
                }
                strip[0, ledSlot] = (byte)(outerRamp[0, rampPos] * bright >> 8);
                strip[1, ledSlot] = (byte)(outerRamp[1, rampPos] * bright >> 8);
                strip[2, ledSlot] = (byte)(outerRamp[2, rampPos] * bright >> 8);
                rampPos += rampStep;
                if (rampPos >= 96)
                {
                    rampPos -= 96;
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
        return frame;
    }
    internal int InfRainbowMorph(int zone, int fans, int bright, int direction, int attach)
    {
        byte red = byte.MaxValue;
        byte green = 0;
        byte blue = 0;
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
        int zoneLeds = zoneLedCountByFans[zone, fans - 1];
        for (int step = 0; step < 255; step++)
        {
            for (int led = 0; led < zoneLeds; led++)
            {
                strip[0, led] = (byte)(red * (byte)bright >> 8);
                strip[1, led] = (byte)(green * (byte)bright >> 8);
                strip[2, led] = (byte)(blue * (byte)bright >> 8);
            }
            if (step < 85)
            {
                red -= 3;
                green += 3;
                blue = 0;
            }
            else if (step < 170)
            {
                red = 0;
                green -= 3;
                blue += 3;
            }
            else
            {
                red += 3;
                green = 0;
                blue -= 3;
            }
            int frame = step;
            for (int fan = 0; fan < fans; fan++)
            {
                int fanBase = fan * 44;
                int ringIdx = fan * 8;
                for (int led = fanBase; led < fanBase + 8; led++)
                {
                    frames[frame, 0, led] = 0;
                    frames[frame, 1, led] = 0;
                    frames[frame, 2, led] = 0;
                    if (zone == 2)
                    {
                        frames[frame, 0, led] = strip[0, ringIdx];
                        frames[frame, 1, led] = strip[1, ringIdx];
                        frames[frame, 2, led] = strip[2, ringIdx];
                        ringIdx++;
                    }
                }
                ringIdx = fan * 10;
                for (int led = fanBase + 8; led < fanBase + 18; led++)
                {
                    frames[frame, 0, led] = 0;
                    frames[frame, 1, led] = 0;
                    frames[frame, 2, led] = 0;
                    if (zone == 1)
                    {
                        frames[frame, 0, led] = strip[0, ringIdx];
                        frames[frame, 1, led] = strip[1, ringIdx];
                        frames[frame, 2, led] = strip[2, ringIdx];
                        ringIdx++;
                    }
                }
                ringIdx = fan * 8;
                for (int led = fanBase + 18; led < fanBase + 26; led++)
                {
                    frames[frame, 0, led] = 0;
                    frames[frame, 1, led] = 0;
                    frames[frame, 2, led] = 0;
                    if (zone == 0)
                    {
                        frames[frame, 0, led] = strip[0, ringIdx];
                        frames[frame, 1, led] = strip[1, ringIdx];
                        frames[frame, 2, led] = strip[2, ringIdx];
                        ringIdx++;
                    }
                }
                ringIdx = fan * 10;
                for (int led = fanBase + 26; led < fanBase + 36; led++)
                {
                    frames[frame, 0, led] = 0;
                    frames[frame, 1, led] = 0;
                    frames[frame, 2, led] = 0;
                    if (zone == 1)
                    {
                        frames[frame, 0, led] = strip[0, ringIdx];
                        frames[frame, 1, led] = strip[1, ringIdx];
                        frames[frame, 2, led] = strip[2, ringIdx];
                        ringIdx++;
                    }
                }
                ringIdx = fan * 8;
                for (int led = fanBase + 36; led < fanBase + 44; led++)
                {
                    frames[frame, 0, led] = 0;
                    frames[frame, 1, led] = 0;
                    frames[frame, 2, led] = 0;
                    if (zone == 0)
                    {
                        frames[frame, 0, led] = strip[0, ringIdx];
                        frames[frame, 1, led] = strip[1, ringIdx];
                        frames[frame, 2, led] = strip[2, ringIdx];
                        ringIdx++;
                    }
                }
            }
        }
        int outFrames = 127;
        byte[,,] halved = new byte[outFrames, 3, ledCount];
        for (int frameOut = 0; frameOut < outFrames; frameOut++)
        {
            for (int channel = 0; channel < 3; channel++)
            {
                for (int ledIdx = 0; ledIdx < ledCount; ledIdx++)
                {
                    halved[frameOut, channel, ledIdx] = frames[frameOut * 2, channel, ledIdx];
                }
            }
        }
        frames = halved;
        return outFrames;
    }
    internal int InfStaticColor(int zone, int fans, int bright, int direction, int attach)
    {
        frames = new byte[30, 3, fans * 44];
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
        int zoneLeds = zoneLedCountByFans[zone, 0];
        for (byte fan = 0; fan < fans; fan++)
        {
            for (int led = fan * zoneLeds; led < (fan + 1) * zoneLeds; led++)
            {
                strip[0, led] = (byte)(palette[zone, fan, 0] * (byte)bright >> 8);
                strip[1, led] = (byte)(palette[zone, fan, 1] * (byte)bright >> 8);
                strip[2, led] = (byte)(palette[zone, fan, 2] * (byte)bright >> 8);
            }
        }
        int frame = 0;
        for (frame = 0; frame < 30; frame++)
        {
            for (int fan = 0; fan < fans; fan++)
            {
                int fanBase = fan * 44;
                int ringIdx = fan * 8;
                for (int led = fanBase; led < fanBase + 8; led++)
                {
                    frames[frame, 0, led] = 0;
                    frames[frame, 1, led] = 0;
                    frames[frame, 2, led] = 0;
                    if (zone == 2)
                    {
                        frames[frame, 0, led] = strip[0, ringIdx];
                        frames[frame, 1, led] = strip[1, ringIdx];
                        frames[frame, 2, led] = strip[2, ringIdx];
                        ringIdx++;
                    }
                }
                ringIdx = fan * 10;
                for (int led = fanBase + 8; led < fanBase + 18; led++)
                {
                    frames[frame, 0, led] = 0;
                    frames[frame, 1, led] = 0;
                    frames[frame, 2, led] = 0;
                    if (zone == 1)
                    {
                        frames[frame, 0, led] = strip[0, ringIdx];
                        frames[frame, 1, led] = strip[1, ringIdx];
                        frames[frame, 2, led] = strip[2, ringIdx];
                        ringIdx++;
                    }
                }
                ringIdx = fan * 8;
                for (int led = fanBase + 18; led < fanBase + 26; led++)
                {
                    frames[frame, 0, led] = 0;
                    frames[frame, 1, led] = 0;
                    frames[frame, 2, led] = 0;
                    if (zone == 0)
                    {
                        frames[frame, 0, led] = strip[0, ringIdx];
                        frames[frame, 1, led] = strip[1, ringIdx];
                        frames[frame, 2, led] = strip[2, ringIdx];
                        ringIdx++;
                    }
                }
                ringIdx = fan * 10;
                for (int led = fanBase + 26; led < fanBase + 36; led++)
                {
                    frames[frame, 0, led] = 0;
                    frames[frame, 1, led] = 0;
                    frames[frame, 2, led] = 0;
                    if (zone == 1)
                    {
                        frames[frame, 0, led] = strip[0, ringIdx];
                        frames[frame, 1, led] = strip[1, ringIdx];
                        frames[frame, 2, led] = strip[2, ringIdx];
                        ringIdx++;
                    }
                }
                ringIdx = fan * 8;
                for (int led = fanBase + 36; led < fanBase + 44; led++)
                {
                    frames[frame, 0, led] = 0;
                    frames[frame, 1, led] = 0;
                    frames[frame, 2, led] = 0;
                    if (zone == 0)
                    {
                        frames[frame, 0, led] = strip[0, ringIdx];
                        frames[frame, 1, led] = strip[1, ringIdx];
                        frames[frame, 2, led] = strip[2, ringIdx];
                        ringIdx++;
                    }
                }
            }
        }
        return 30;
    }
    internal int InfBreathing(int zone, int fans, int bright, int direction, int attach)
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
        int zoneLeds = zoneLedCountByFans[zone, fans - 1];
        int phase = 0;
        for (int frame = 0; frame < 170; frame++)
        {
            byte pulse = (byte)((phase * 3) & 0xFF);
            for (int led = 0; led < zoneLeds; led++)
            {
                int colorIdx = 0;
                if (led >= zoneLedCountByFans[zone, 0] && led < zoneLedCountByFans[zone, 1])
                {
                    colorIdx = 1;
                }
                else if (led >= zoneLedCountByFans[zone, 1] && led < zoneLedCountByFans[zone, 2])
                {
                    colorIdx = 2;
                }
                else if (led >= zoneLedCountByFans[zone, 2] && led < zoneLedCountByFans[zone, 3])
                {
                    colorIdx = 3;
                }
                byte red = (byte)(palette[zone, colorIdx, 0] * pulse >> 8);
                byte green = (byte)(palette[zone, colorIdx, 1] * pulse >> 8);
                byte blue = (byte)(palette[zone, colorIdx, 2] * pulse >> 8);
                strip[0, led] = (byte)(red * (byte)bright >> 8);
                strip[1, led] = (byte)(green * (byte)bright >> 8);
                strip[2, led] = (byte)(blue * (byte)bright >> 8);
            }
            phase = ((frame >= 85) ? (phase - 1) : (phase + 1));
            int frameIdx = frame;
            for (int fan = 0; fan < fans; fan++)
            {
                int fanBase = fan * 44;
                int ringIdx = fan * 8;
                for (int led = fanBase; led < fanBase + 8; led++)
                {
                    frames[frameIdx, 0, led] = 0;
                    frames[frameIdx, 1, led] = 0;
                    frames[frameIdx, 2, led] = 0;
                    if (zone == 2)
                    {
                        frames[frameIdx, 0, led] = strip[0, ringIdx];
                        frames[frameIdx, 1, led] = strip[1, ringIdx];
                        frames[frameIdx, 2, led] = strip[2, ringIdx];
                        ringIdx++;
                    }
                }
                ringIdx = fan * 10;
                for (int led = fanBase + 8; led < fanBase + 18; led++)
                {
                    frames[frameIdx, 0, led] = 0;
                    frames[frameIdx, 1, led] = 0;
                    frames[frameIdx, 2, led] = 0;
                    if (zone == 1)
                    {
                        frames[frameIdx, 0, led] = strip[0, ringIdx];
                        frames[frameIdx, 1, led] = strip[1, ringIdx];
                        frames[frameIdx, 2, led] = strip[2, ringIdx];
                        ringIdx++;
                    }
                }
                ringIdx = fan * 8;
                for (int led = fanBase + 18; led < fanBase + 26; led++)
                {
                    frames[frameIdx, 0, led] = 0;
                    frames[frameIdx, 1, led] = 0;
                    frames[frameIdx, 2, led] = 0;
                    if (zone == 0)
                    {
                        frames[frameIdx, 0, led] = strip[0, ringIdx];
                        frames[frameIdx, 1, led] = strip[1, ringIdx];
                        frames[frameIdx, 2, led] = strip[2, ringIdx];
                        ringIdx++;
                    }
                }
                ringIdx = fan * 10;
                for (int led = fanBase + 26; led < fanBase + 36; led++)
                {
                    frames[frameIdx, 0, led] = 0;
                    frames[frameIdx, 1, led] = 0;
                    frames[frameIdx, 2, led] = 0;
                    if (zone == 1)
                    {
                        frames[frameIdx, 0, led] = strip[0, ringIdx];
                        frames[frameIdx, 1, led] = strip[1, ringIdx];
                        frames[frameIdx, 2, led] = strip[2, ringIdx];
                        ringIdx++;
                    }
                }
                ringIdx = fan * 8;
                for (int led = fanBase + 36; led < fanBase + 44; led++)
                {
                    frames[frameIdx, 0, led] = 0;
                    frames[frameIdx, 1, led] = 0;
                    frames[frameIdx, 2, led] = 0;
                    if (zone == 0)
                    {
                        frames[frameIdx, 0, led] = strip[0, ringIdx];
                        frames[frameIdx, 1, led] = strip[1, ringIdx];
                        frames[frameIdx, 2, led] = strip[2, ringIdx];
                        ringIdx++;
                    }
                }
            }
        }
        return 170;
    }
    internal int InfRunway(int zone, int fans, int bright, int direction, int attach)
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
        for (int pass = 0; pass < 2; pass++)
        {
            for (int head = 0; head < zoneLeds + fans + fans - 1; head++)
            {
                for (int led = 0; led < zoneLeds; led++)
                {
                    int colorIdx = 1;
                    if (led <= head && led + fans + fans > head)
                    {
                        colorIdx = 0;
                    }
                    byte red = palette[zone, colorIdx, 0];
                    byte green = palette[zone, colorIdx, 1];
                    byte blue = palette[zone, colorIdx, 2];
                    int stripIdx = led;
                    if (pass == 1)
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
    internal int InfMeteor(int zone, int fans, int bright, int direction, int attach)
    {
        byte[,] strip = new byte[3, 176];
        byte[,,] zoneLedCountByFans = new byte[3, 1, 4]
        {
            { { 8, 16, 24, 32 } },
            { { 10, 20, 30, 40 } },
            { { 8, 16, 24, 32 } }
        };
        byte[,,] tailFade = new byte[4, 1, 8]
        {
            { { 32, 255, 0, 0, 0, 0, 0, 0 } },
            { { 16, 64, 128, 255, 0, 0, 0, 0 } },
            { { 8, 16, 32, 64, 128, 255, 0, 0 } },
            { { 6, 10, 16, 32, 64, 96, 168, 255 } }
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
            for (int head = 0; head < zoneLeds + fans + fans - 1; head++)
            {
                int tailPos = 0;
                for (int led = 0; led < zoneLeds; led++)
                {
                    byte red = 0;
                    byte green = 0;
                    byte blue = 0;
                    if (led <= head && led + fans + fans > head)
                    {
                        red = (byte)(palette[zone, colorIdx, 0] * tailFade[fans - 1, 0, tailPos] >> 8);
                        green = (byte)(palette[zone, colorIdx, 1] * tailFade[fans - 1, 0, tailPos] >> 8);
                        blue = (byte)(palette[zone, colorIdx, 2] * tailFade[fans - 1, 0, tailPos] >> 8);
                        tailPos++;
                    }
                    int stripIdx = led;
                    if (direction == 1)
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
    internal int InfTwinkle(int zone, int fans, int bright, int direction, int attach)
    {
        byte[,] ledColors = new byte[3, 174];
        byte[] paletteRowByLed = new byte[174]
        {
            1, 0, 2, 3, 1, 2, 1, 0, 2, 3,
            0, 3, 1, 0, 1, 2, 0, 2, 1, 0,
            3, 1, 3, 0, 1, 0, 2, 0, 1, 3,
            1, 2, 3, 1, 2, 1, 0, 2, 1, 2,
            0, 2, 0, 1, 3, 0, 2, 3, 1, 0,
            1, 2, 3, 0, 1, 2, 1, 3, 0, 3,
            2, 1, 0, 3, 0, 2, 1, 0, 3, 0,
            1, 2, 0, 3, 2, 1, 0, 1, 2, 3,
            0, 1, 2, 1, 3, 2, 1, 0, 1, 3,
            2, 0, 2, 1, 0, 1, 3, 2, 1, 0,
            2, 3, 0, 1, 3, 0, 2, 3, 1, 2,
            0, 3, 1, 2, 3, 2, 0, 1, 2, 3,
            1, 2, 1, 2, 0, 1, 2, 0, 1, 3,
            0, 2, 3, 1, 2, 0, 1, 2, 3, 2,
            1, 2, 0, 1, 3, 0, 1, 2, 1, 2,
            3, 2, 0, 2, 3, 0, 1, 3, 0, 2,
            0, 1, 3, 0, 1, 3, 2, 0, 1, 3,
            0, 2, 1, 0
        };
        byte[,] twinkleLevels = FlexTwinkleTable.Table;
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
        int frame;
        for (frame = 0; frame < 200; frame++)
        {
            for (int led = 0; led < 174 && led < fans * 44; led++)
            {
                ledColors[0, led] = (byte)(palette[2, paletteRowByLed[led], 0] * twinkleLevels[frame, led] >> 8);
                ledColors[1, led] = (byte)(palette[2, paletteRowByLed[led], 1] * twinkleLevels[frame, led] >> 8);
                ledColors[2, led] = (byte)(palette[2, paletteRowByLed[led], 2] * twinkleLevels[frame, led] >> 8);
                int frameIdx = frame;
                frames[frameIdx, 0, led] = (byte)(ledColors[0, led] * (byte)bright >> 8);
                frames[frameIdx, 1, led] = (byte)(ledColors[1, led] * (byte)bright >> 8);
                frames[frameIdx, 2, led] = (byte)(ledColors[2, led] * (byte)bright >> 8);
            }
        }
        return frame;
    }
    internal int InfTaichi(int zone, int fans, int bright, int direction, int attach)
    {
        byte[,] strip = new byte[3, 176];
        byte[] halfPattern8 = new byte[8] { 0, 0, 0, 0, 1, 1, 1, 1 };
        byte[] halfPattern10 = new byte[10] { 0, 0, 0, 0, 0, 1, 1, 1, 1, 1 };
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
        int zoneLeds = 8;
        if (zone == 1)
        {
            zoneLeds = 10;
        }
        int frame = 0;
        for (int shift = 0; shift < zoneLeds; shift++)
        {
            for (int led = 0; led < zoneLeds; led++)
            {
                int patternIdx = shift + led;
                if (patternIdx >= zoneLeds)
                {
                    patternIdx -= zoneLeds;
                }
                byte paletteRow = ((zone != 1) ? halfPattern8[patternIdx] : halfPattern10[patternIdx]);
                byte red = (byte)(palette[zone, paletteRow, 0] * (byte)bright >> 8);
                byte green = (byte)(palette[zone, paletteRow, 1] * (byte)bright >> 8);
                byte blue = (byte)(palette[zone, paletteRow, 2] * (byte)bright >> 8);
                int stripIdx = led;
                if (direction == 1)
                {
                    stripIdx = zoneLeds - led - 1;
                }
                strip[0, stripIdx] = red;
                strip[1, stripIdx] = green;
                strip[2, stripIdx] = blue;
            }
            for (byte copyFan = 1; copyFan < fans; copyFan++)
            {
                for (int led = 0; led < zoneLeds; led++)
                {
                    strip[0, copyFan * zoneLeds + led] = strip[0, led];
                    strip[1, copyFan * zoneLeds + led] = strip[1, led];
                    strip[2, copyFan * zoneLeds + led] = strip[2, led];
                }
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
        return frame;
    }
}
