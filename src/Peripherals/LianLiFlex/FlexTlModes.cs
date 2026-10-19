using System;

namespace Nexus.Service.Peripherals.LianLiFlex;

internal sealed partial class FlexTlModes
{
    private byte[,,] frames;
    private readonly byte[,,] palette;
    private readonly int ledCount;

    public FlexTlModes(int fans, byte[,,] palette)
    {
        this.palette = palette;
        ledCount = fans * 26;
        frames = new byte[1024, 3, ledCount];
    }

    public byte[,,] Frames => frames;

    internal int TlRainbow(int tb, int fans, int bright, int direction)
    {
        frames = new byte[fans * 13, 3, fans * 26];
        byte[,] ledBuf = new byte[3, 104];
        byte[,] table1Fan = new byte[3, 15]
        {
            {
                255, 204, 153, 102, 51, 0, 0, 0, 0, 0,
                0, 51, 102, 153, 204
            },
            {
                0, 51, 102, 153, 204, 255, 204, 153, 102, 51,
                0, 0, 0, 0, 0
            },
            {
                0, 0, 0, 0, 0, 0, 51, 102, 153, 204,
                255, 204, 153, 102, 51
            }
        };
        byte[,] table2Fans = new byte[3, 27]
        {
            {
                255, 226, 197, 168, 139, 110, 81, 52, 23, 0,
                0, 0, 0, 0, 0, 0, 0, 0, 0, 23,
                52, 81, 110, 139, 168, 197, 226
            },
            {
                0, 23, 52, 81, 110, 139, 168, 197, 226, 255,
                226, 197, 168, 139, 110, 81, 52, 23, 0, 0,
                0, 0, 0, 0, 0, 0, 0
            },
            {
                0, 0, 0, 0, 0, 0, 0, 0, 0, 0,
                23, 52, 81, 110, 139, 168, 197, 226, 255, 226,
                197, 168, 139, 110, 81, 52, 23
            }
        };
        byte[,] table3Fans = new byte[3, 39]
        {
            {
                255, 235, 215, 195, 175, 155, 135, 115, 95, 75,
                55, 35, 15, 0, 0, 0, 0, 0, 0, 0,
                0, 0, 0, 0, 0, 0, 0, 15, 35, 55,
                75, 95, 115, 135, 155, 175, 195, 215, 235
            },
            {
                0, 15, 35, 55, 75, 95, 115, 135, 155, 175,
                195, 215, 235, 255, 235, 215, 195, 175, 155, 135,
                115, 95, 75, 55, 35, 15, 0, 0, 0, 0,
                0, 0, 0, 0, 0, 0, 0, 0, 0
            },
            {
                0, 0, 0, 0, 0, 0, 0, 0, 0, 0,
                0, 0, 0, 0, 15, 35, 55, 75, 95, 115,
                135, 155, 175, 195, 215, 235, 255, 235, 215, 195,
                175, 155, 135, 115, 95, 75, 55, 35, 15
            }
        };
        byte[,] table4Fans = new byte[3, 54]
        {
            {
                255, 241, 227, 213, 199, 185, 171, 157, 143, 129,
                115, 101, 87, 73, 59, 45, 31, 17, 0, 0,
                0, 0, 0, 0, 0, 0, 0, 0, 0, 0,
                0, 0, 0, 0, 0, 0, 0, 17, 31, 45,
                59, 73, 87, 101, 115, 129, 143, 157, 171, 185,
                199, 213, 227, 241
            },
            {
                0, 17, 31, 45, 59, 73, 87, 101, 115, 129,
                143, 157, 171, 185, 199, 213, 227, 241, 255, 241,
                227, 213, 199, 185, 171, 157, 143, 129, 115, 101,
                87, 73, 59, 45, 31, 17, 0, 0, 0, 0,
                0, 0, 0, 0, 0, 0, 0, 0, 0, 0,
                0, 0, 0, 0
            },
            {
                0, 0, 0, 0, 0, 0, 0, 0, 0, 0,
                0, 0, 0, 0, 0, 0, 0, 0, 0, 17,
                31, 45, 59, 73, 87, 101, 115, 129, 143, 157,
                171, 185, 199, 213, 227, 241, 255, 241, 227, 213,
                199, 185, 171, 157, 143, 129, 115, 101, 87, 73,
                59, 45, 31, 17
            }
        };
        if (tb >= 2)
        {
            tb = 0;
        }
        if (fans == 0 || fans > 4)
        {
            fans = 4;
        }
        if (direction > 1)
        {
            direction = 0;
        }
        int zoneLeds = fans * 13;
        int frameCount = 0;
        for (int i = 0; i < zoneLeds; i++)
        {
            int cursor = i;
            for (int j = 0; j < zoneLeds; j++)
            {
                int red = table4Fans[0, cursor];
                int green = table4Fans[1, cursor];
                int blue = table4Fans[2, cursor];
                int tableLen = 54;
                if (fans == 1)
                {
                    red = table1Fan[0, cursor];
                    green = table1Fan[1, cursor];
                    blue = table1Fan[2, cursor];
                    tableLen = 15;
                }
                if (fans == 2)
                {
                    red = table2Fans[0, cursor];
                    green = table2Fans[1, cursor];
                    blue = table2Fans[2, cursor];
                    tableLen = 27;
                }
                if (fans == 3)
                {
                    red = table3Fans[0, cursor];
                    green = table3Fans[1, cursor];
                    blue = table3Fans[2, cursor];
                    tableLen = 39;
                }
                int outIndex = j;
                if (direction == 0)
                {
                    outIndex = zoneLeds - j - 1;
                }
                ledBuf[0, outIndex] = (byte)(red * bright >> 8);
                ledBuf[1, outIndex] = (byte)(green * bright >> 8);
                ledBuf[2, outIndex] = (byte)(blue * bright >> 8);
                cursor++;
                if (cursor >= tableLen)
                {
                    cursor -= tableLen;
                }
            }
            int frameStart = frameCount;
            for (cursor = 0; cursor < fans; cursor++)
            {
                int fanBase = cursor * 26;
                int srcIndex = cursor * 13;
                for (int j = fanBase; j < fanBase + 13; j++)
                {
                    frames[frameStart, 0, j] = 0;
                    frames[frameStart, 1, j] = 0;
                    frames[frameStart, 2, j] = 0;
                    if (tb == 0)
                    {
                        frames[frameStart, 0, j] = ledBuf[0, srcIndex];
                        frames[frameStart, 1, j] = ledBuf[1, srcIndex];
                        frames[frameStart, 2, j] = ledBuf[2, srcIndex];
                        srcIndex++;
                    }
                }
                srcIndex = cursor * 13;
                for (int j = fanBase + 13; j < fanBase + 26; j++)
                {
                    frames[frameStart, 0, j] = 0;
                    frames[frameStart, 1, j] = 0;
                    frames[frameStart, 2, j] = 0;
                    if (tb == 1)
                    {
                        frames[frameStart, 0, j] = ledBuf[0, srcIndex];
                        frames[frameStart, 1, j] = ledBuf[1, srcIndex];
                        frames[frameStart, 2, j] = ledBuf[2, srcIndex];
                        srcIndex++;
                    }
                }
            }
            frameCount++;
        }
        return frameCount;
    }
    internal int TlRainbowMorph(int tb, int fans, int bright, int direction)
    {
        frames = new byte[255, 3, fans * 26];
        byte[,] ledBuf = new byte[3, 104];
        if (tb >= 2)
        {
            tb = 0;
        }
        if (fans == 0 || fans > 4)
        {
            fans = 4;
        }
        if (direction > 1)
        {
            direction = 0;
        }
        int totalLeds = fans * 26;
        int red = 255;
        int green = 0;
        int blue = 0;
        int frameIndex = 0;
        for (int i = 0; i < 255; i++)
        {
            for (int j = 0; j < totalLeds; j++)
            {
                ledBuf[0, j] = (byte)(red * bright >> 8);
                ledBuf[1, j] = (byte)(green * bright >> 8);
                ledBuf[2, j] = (byte)(blue * bright >> 8);
            }
            if (i < 85)
            {
                red -= 3;
                green += 3;
                blue = 0;
            }
            else if (i < 170)
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
            int frameStart = frameIndex;
            for (int k = 0; k < fans; k++)
            {
                int fanBase = k * 26;
                int srcIndex = k * 13;
                for (int j = fanBase; j < fanBase + 13; j++)
                {
                    frames[frameIndex, 0, j] = 0;
                    frames[frameIndex, 1, j] = 0;
                    frames[frameIndex, 2, j] = 0;
                    if (tb == 0)
                    {
                        frames[frameIndex, 0, j] = ledBuf[0, srcIndex];
                        frames[frameIndex, 1, j] = ledBuf[1, srcIndex];
                        frames[frameIndex, 2, j] = ledBuf[2, srcIndex];
                        srcIndex++;
                    }
                }
                srcIndex = k * 13;
                for (int j = fanBase + 13; j < fanBase + 26; j++)
                {
                    frames[frameIndex, 0, j] = 0;
                    frames[frameIndex, 1, j] = 0;
                    frames[frameIndex, 2, j] = 0;
                    if (tb == 1)
                    {
                        frames[frameIndex, 0, j] = ledBuf[0, srcIndex];
                        frames[frameIndex, 1, j] = ledBuf[1, srcIndex];
                        frames[frameIndex, 2, j] = ledBuf[2, srcIndex];
                        srcIndex++;
                    }
                }
            }
            frameIndex++;
        }
        int keptFrames = frameIndex / 2;
        byte[,,] thinned = new byte[keptFrames, 3, ledCount];
        for (int l = 0; l < keptFrames; l++)
        {
            for (int m = 0; m < 3; m++)
            {
                for (int n = 0; n < ledCount; n++)
                {
                    thinned[l, m, n] = frames[l * 2, m, n];
                }
            }
        }
        frames = thinned;
        return keptFrames;
    }
    internal int TlStaticColor(int tb, int fans, int bright, int direction)
    {
        frames = new byte[30, 3, fans * 26];
        byte[,] ledBuf = new byte[3, 104];
        if (tb >= 2)
        {
            tb = 0;
        }
        if (fans == 0 || fans > 4)
        {
            fans = 4;
        }
        if (direction > 1)
        {
            direction = 0;
        }
        for (int i = 0; i < 4; i++)
        {
            for (int j = i * 13; j < (i + 1) * 13; j++)
            {
                ledBuf[0, j] = (byte)(palette[tb, i, 0] * bright >> 8);
                ledBuf[1, j] = (byte)(palette[tb, i, 1] * bright >> 8);
                ledBuf[2, j] = (byte)(palette[tb, i, 2] * bright >> 8);
            }
        }
        int frameIndex = 0;
        for (frameIndex = 0; frameIndex < 30; frameIndex++)
        {
            for (int k = 0; k < fans; k++)
            {
                int fanBase = k * 26;
                int srcIndex = k * 13;
                for (int j = fanBase; j < fanBase + 13; j++)
                {
                    frames[frameIndex, 0, j] = 0;
                    frames[frameIndex, 1, j] = 0;
                    frames[frameIndex, 2, j] = 0;
                    if (tb == 0)
                    {
                        frames[frameIndex, 0, j] = ledBuf[0, srcIndex];
                        frames[frameIndex, 1, j] = ledBuf[1, srcIndex];
                        frames[frameIndex, 2, j] = ledBuf[2, srcIndex];
                        srcIndex++;
                    }
                }
                srcIndex = k * 13;
                for (int j = fanBase + 13; j < fanBase + 26; j++)
                {
                    frames[frameIndex, 0, j] = 0;
                    frames[frameIndex, 1, j] = 0;
                    frames[frameIndex, 2, j] = 0;
                    if (tb == 1)
                    {
                        frames[frameIndex, 0, j] = ledBuf[0, srcIndex];
                        frames[frameIndex, 1, j] = ledBuf[1, srcIndex];
                        frames[frameIndex, 2, j] = ledBuf[2, srcIndex];
                        srcIndex++;
                    }
                }
            }
        }
        return 30;
    }
    internal int TlBreathing(int tb, int fans, int bright, int direction)
    {
        byte[,] ledBuf = new byte[3, 104];
        if (tb >= 2)
        {
            tb = 0;
        }
        if (fans == 0 || fans > 4)
        {
            fans = 4;
        }
        if (direction > 1)
        {
            direction = 0;
        }
        int zoneLeds = fans * 13;
        int frameCount = 0;
        int phase = 0;
        for (int i = 0; i < 170; i++)
        {
            int level = (phase * 3) & 0xFF;
            for (int j = 0; j < zoneLeds; j++)
            {
                int colorIndex = 0;
                if (j >= 13 && j < 26)
                {
                    colorIndex = 1;
                }
                else if (j >= 26 && j < 39)
                {
                    colorIndex = 2;
                }
                else if (j >= 39 && j < 54)
                {
                    colorIndex = 3;
                }
                int scaledRed = palette[tb, colorIndex, 0] * level >> 8;
                int scaledGreen = palette[tb, colorIndex, 1] * level >> 8;
                int scaledBlue = palette[tb, colorIndex, 2] * level >> 8;
                ledBuf[0, j] = (byte)(scaledRed * bright >> 8);
                ledBuf[1, j] = (byte)(scaledGreen * bright >> 8);
                ledBuf[2, j] = (byte)(scaledBlue * bright >> 8);
            }
            phase = ((i >= 85) ? (phase - 1) : (phase + 1));
            int frameStart = frameCount;
            for (int k = 0; k < fans; k++)
            {
                int fanBase = k * 26;
                int srcIndex = k * 13;
                for (int j = fanBase; j < fanBase + 13; j++)
                {
                    frames[frameStart, 0, j] = 0;
                    frames[frameStart, 1, j] = 0;
                    frames[frameStart, 2, j] = 0;
                    if (tb == 0)
                    {
                        frames[frameStart, 0, j] = ledBuf[0, srcIndex];
                        frames[frameStart, 1, j] = ledBuf[1, srcIndex];
                        frames[frameStart, 2, j] = ledBuf[2, srcIndex];
                        srcIndex++;
                    }
                }
                srcIndex = k * 13;
                for (int j = fanBase + 13; j < fanBase + 26; j++)
                {
                    frames[frameStart, 0, j] = 0;
                    frames[frameStart, 1, j] = 0;
                    frames[frameStart, 2, j] = 0;
                    if (tb == 1)
                    {
                        frames[frameStart, 0, j] = ledBuf[0, srcIndex];
                        frames[frameStart, 1, j] = ledBuf[1, srcIndex];
                        frames[frameStart, 2, j] = ledBuf[2, srcIndex];
                        srcIndex++;
                    }
                }
            }
            frameCount++;
        }
        return frameCount;
    }
    internal int TlRunway(int tb, int fans, int bright, int direction)
    {
        byte[,] ledBuf = new byte[3, 104];
        if (tb >= 2)
        {
            tb = 0;
        }
        if (fans == 0 || fans > 4)
        {
            fans = 4;
        }
        if (direction > 1)
        {
            direction = 0;
        }
        int zoneLeds = fans * 13;
        int frameCount = 0;
        for (int i = 0; i < 2; i++)
        {
            for (int j = 0; j < zoneLeds + fans + fans - 1; j++)
            {
                for (int k = 0; k < zoneLeds; k++)
                {
                    int colorIndex = 1;
                    if (k <= j && k + fans + fans > j)
                    {
                        colorIndex = 0;
                    }
                    int red = palette[tb, colorIndex, 0];
                    int green = palette[tb, colorIndex, 1];
                    int blue = palette[tb, colorIndex, 2];
                    int outIndex = k;
                    if (i != 0)
                    {
                        outIndex = zoneLeds - k - 1;
                    }
                    ledBuf[0, outIndex] = (byte)(red * bright >> 8);
                    ledBuf[1, outIndex] = (byte)(green * bright >> 8);
                    ledBuf[2, outIndex] = (byte)(blue * bright >> 8);
                }
                int frameStart = frameCount;
                for (int l = 0; l < fans; l++)
                {
                    int fanBase = l * 26;
                    int srcIndex = l * 13;
                    for (int k = fanBase; k < fanBase + 13; k++)
                    {
                        frames[frameCount, 0, k] = 0;
                        frames[frameCount, 1, k] = 0;
                        frames[frameCount, 2, k] = 0;
                        if (tb == 0)
                        {
                            frames[frameCount, 0, k] = ledBuf[0, srcIndex];
                            frames[frameCount, 1, k] = ledBuf[1, srcIndex];
                            frames[frameCount, 2, k] = ledBuf[2, srcIndex];
                            srcIndex++;
                        }
                    }
                    srcIndex = l * 13;
                    for (int k = fanBase + 13; k < fanBase + 26; k++)
                    {
                        frames[frameCount, 0, k] = 0;
                        frames[frameCount, 1, k] = 0;
                        frames[frameCount, 2, k] = 0;
                        if (tb == 1)
                        {
                            frames[frameCount, 0, k] = ledBuf[0, srcIndex];
                            frames[frameCount, 1, k] = ledBuf[1, srcIndex];
                            frames[frameCount, 2, k] = ledBuf[2, srcIndex];
                            srcIndex++;
                        }
                    }
                }
                frameCount++;
            }
        }
        return frameCount;
    }
    internal int TlMeteor(int tb, int fans, int bright, int direction)
    {
        byte[,] ledBuf = new byte[3, 104];
        byte[,] tailProfile = new byte[4, 8]
        {
            { 32, 255, 0, 0, 0, 0, 0, 0 },
            { 16, 64, 128, 255, 0, 0, 0, 0 },
            { 8, 16, 32, 64, 128, 255, 0, 0 },
            { 6, 10, 16, 32, 64, 96, 168, 255 }
        };
        if (tb >= 2)
        {
            tb = 0;
        }
        if (fans == 0 || fans > 4)
        {
            fans = 4;
        }
        if (direction > 1)
        {
            direction = 0;
        }
        int zoneLeds = fans * 13;
        int frameCount = 0;
        for (int i = 0; i < 4; i++)
        {
            for (int j = 0; j < zoneLeds + fans + fans - 1; j++)
            {
                int tailPos = 0;
                for (int k = 0; k < zoneLeds; k++)
                {
                    int red = 0;
                    int green = 0;
                    int blue = 0;
                    if (k <= j && k + fans + fans > j)
                    {
                        red = palette[tb, i, 0] * tailProfile[fans - 1, tailPos] >> 8;
                        green = palette[tb, i, 1] * tailProfile[fans - 1, tailPos] >> 8;
                        blue = palette[tb, i, 2] * tailProfile[fans - 1, tailPos] >> 8;
                        tailPos++;
                    }
                    int outIndex = k;
                    if (direction != 0)
                    {
                        outIndex = zoneLeds - k - 1;
                    }
                    ledBuf[0, outIndex] = (byte)(red * bright >> 8);
                    ledBuf[1, outIndex] = (byte)(green * bright >> 8);
                    ledBuf[2, outIndex] = (byte)(blue * bright >> 8);
                }
                int frameStart = frameCount;
                for (int l = 0; l < fans; l++)
                {
                    int fanBase = l * 26;
                    int srcIndex = l * 13;
                    for (int k = fanBase; k < fanBase + 13; k++)
                    {
                        frames[frameCount, 0, k] = 0;
                        frames[frameCount, 1, k] = 0;
                        frames[frameCount, 2, k] = 0;
                        if (tb == 0)
                        {
                            frames[frameCount, 0, k] = ledBuf[0, srcIndex];
                            frames[frameCount, 1, k] = ledBuf[1, srcIndex];
                            frames[frameCount, 2, k] = ledBuf[2, srcIndex];
                            srcIndex++;
                        }
                    }
                    srcIndex = l * 13;
                    for (int k = fanBase + 13; k < fanBase + 26; k++)
                    {
                        frames[frameCount, 0, k] = 0;
                        frames[frameCount, 1, k] = 0;
                        frames[frameCount, 2, k] = 0;
                        if (tb == 1)
                        {
                            frames[frameCount, 0, k] = ledBuf[0, srcIndex];
                            frames[frameCount, 1, k] = ledBuf[1, srcIndex];
                            frames[frameCount, 2, k] = ledBuf[2, srcIndex];
                            srcIndex++;
                        }
                    }
                }
                frameCount++;
            }
        }
        return frameCount;
    }
    internal int TlColorCycle(int tb, int fans, int bright, int direction)
    {
        byte[,,] cycleBuf = new byte[2, 3, 104];
        byte[] gapLengths = new byte[4] { 3, 6, 8, 9 };
        byte[] litLengths = new byte[4] { 1, 2, 5, 8 };
        if (tb >= 2)
        {
            tb = 0;
        }
        if (fans == 0 || fans > 4)
        {
            fans = 4;
        }
        if (direction > 1)
        {
            direction = 0;
        }
        int zoneLeds = fans * 13;
        int rangeStart = 0;
        int rangeEnd = 0;
        for (int i = 0; i < 3; i++)
        {
            rangeStart = rangeEnd;
            rangeEnd += litLengths[fans - 1];
            for (int j = rangeStart; j < rangeEnd; j++)
            {
                cycleBuf[0, 0, j] = palette[tb, i, 0];
                cycleBuf[0, 1, j] = palette[tb, i, 1];
                cycleBuf[0, 2, j] = palette[tb, i, 2];
            }
            rangeStart = rangeEnd;
            rangeEnd += gapLengths[fans - 1];
            for (int j = rangeStart; j < rangeEnd; j++)
            {
                cycleBuf[0, 0, j] = 0;
                cycleBuf[0, 1, j] = 0;
                cycleBuf[0, 2, j] = 0;
            }
            if ((tb == 0 && fans == 1) || (tb == 0 && fans == 4))
            {
                rangeStart--;
                rangeEnd--;
            }
            if (tb == 0 && fans == 2)
            {
                rangeStart = rangeEnd;
                rangeEnd++;
                cycleBuf[0, 0, rangeStart] = 0;
                cycleBuf[0, 1, rangeStart] = 0;
                cycleBuf[0, 2, rangeStart] = 0;
            }
        }
        int frameIndex = 0;
        for (int k = 0; k < zoneLeds; k++)
        {
            int cursor = k;
            for (int j = 0; j < zoneLeds; j++)
            {
                rangeStart = j;
                if (direction == 0)
                {
                    rangeStart = zoneLeds - j - 1;
                }
                cycleBuf[1, 0, rangeStart] = (byte)(cycleBuf[0, 0, cursor] * bright >> 8);
                cycleBuf[1, 1, rangeStart] = (byte)(cycleBuf[0, 1, cursor] * bright >> 8);
                cycleBuf[1, 2, rangeStart] = (byte)(cycleBuf[0, 2, cursor] * bright >> 8);
                cursor++;
                if (cursor >= zoneLeds)
                {
                    cursor = 0;
                }
            }
            int frameStart = frameIndex;
            for (cursor = 0; cursor < fans; cursor++)
            {
                rangeStart = cursor * 26;
                rangeEnd = cursor * 13;
                for (int j = rangeStart; j < rangeStart + 13; j++)
                {
                    frames[frameIndex, 0, j] = 0;
                    frames[frameIndex, 1, j] = 0;
                    frames[frameIndex, 2, j] = 0;
                    if (tb == 0)
                    {
                        frames[frameIndex, 0, j] = cycleBuf[1, 0, rangeEnd];
                        frames[frameIndex, 1, j] = cycleBuf[1, 1, rangeEnd];
                        frames[frameIndex, 2, j] = cycleBuf[1, 2, rangeEnd];
                        rangeEnd++;
                    }
                }
                rangeEnd = cursor * 13;
                for (int j = rangeStart + 13; j < rangeStart + 26; j++)
                {
                    frames[frameIndex, 0, j] = 0;
                    frames[frameIndex, 1, j] = 0;
                    frames[frameIndex, 2, j] = 0;
                    if (tb == 1)
                    {
                        frames[frameIndex, 0, j] = cycleBuf[1, 0, rangeEnd];
                        frames[frameIndex, 1, j] = cycleBuf[1, 1, rangeEnd];
                        frames[frameIndex, 2, j] = cycleBuf[1, 2, rangeEnd];
                        rangeEnd++;
                    }
                }
            }
            frameIndex++;
        }
        return frameIndex;
    }
    internal int TlStack(int tb, int fans, int bright, int direction)
    {
        byte[,] ledBuf = new byte[3, 104];
        if (tb >= 2)
        {
            tb = 0;
        }
        if (fans == 0 || fans > 4)
        {
            fans = 4;
        }
        if (direction > 1)
        {
            direction = 0;
        }
        int zoneLeds = fans * 13;
        int stackWidth = fans;
        int layerSteps = 13;
        int frameIndex = 0;
        for (int i = 0; i < 4; i++)
        {
            frameIndex = 0;
            int consumed = 0;
            int layer = 0;
            while (layer < layerSteps)
            {
                for (int j = 0; j < zoneLeds - consumed; j++)
                {
                    for (int k = 0; k < zoneLeds - consumed; k++)
                    {
                        int red = 0;
                        int green = 0;
                        int blue = 0;
                        if (k <= j && k + stackWidth > j)
                        {
                            red = palette[tb, i, 0];
                            green = palette[tb, i, 1];
                            blue = palette[tb, i, 2];
                        }
                        int outIndex = k;
                        if (direction != 0)
                        {
                            outIndex = zoneLeds - k - 1;
                        }
                        ledBuf[0, outIndex] = (byte)(red * bright >> 8);
                        ledBuf[1, outIndex] = (byte)(green * bright >> 8);
                        ledBuf[2, outIndex] = (byte)(blue * bright >> 8);
                    }
                    int frameStart = frameIndex;
                    for (int fan = 0; fan < fans; fan++)
                    {
                        int fanBase = fan * 26;
                        int srcIndex = fan * 13;
                        for (int k = fanBase; k < fanBase + 13; k++)
                        {
                            frames[frameIndex, 0, k] = 0;
                            frames[frameIndex, 1, k] = 0;
                            frames[frameIndex, 2, k] = 0;
                            if (tb == 0)
                            {
                                frames[frameIndex, 0, k] = ledBuf[0, srcIndex];
                                frames[frameIndex, 1, k] = ledBuf[1, srcIndex];
                                frames[frameIndex, 2, k] = ledBuf[2, srcIndex];
                                srcIndex++;
                            }
                        }
                        srcIndex = fan * 13;
                        for (int k = fanBase + 13; k < fanBase + 26; k++)
                        {
                            frames[frameIndex, 0, k] = 0;
                            frames[frameIndex, 1, k] = 0;
                            frames[frameIndex, 2, k] = 0;
                            if (tb == 1)
                            {
                                frames[frameIndex, 0, k] = ledBuf[0, srcIndex];
                                frames[frameIndex, 1, k] = ledBuf[1, srcIndex];
                                frames[frameIndex, 2, k] = ledBuf[2, srcIndex];
                                srcIndex++;
                            }
                        }
                    }
                    frameIndex++;
                }
                layer++;
                consumed += stackWidth;
            }
            for (int j = 0; j < zoneLeds; j++)
            {
                for (int k = 0; k < zoneLeds; k++)
                {
                    int red = 0;
                    int green = 0;
                    int blue = 0;
                    if (k > j)
                    {
                        red = palette[tb, i, 0];
                        green = palette[tb, i, 1];
                        blue = palette[tb, i, 2];
                    }
                    int outIndex = k;
                    if (direction != 0)
                    {
                        outIndex = zoneLeds - k - 1;
                    }
                    ledBuf[0, outIndex] = (byte)(red * bright >> 8);
                    ledBuf[1, outIndex] = (byte)(green * bright >> 8);
                    ledBuf[2, outIndex] = (byte)(blue * bright >> 8);
                }
                int frameStart = frameIndex;
                for (int fan = 0; fan < fans; fan++)
                {
                    int fanBase = fan * 26;
                    int srcIndex = fan * 13;
                    for (int k = fanBase; k < fanBase + 13; k++)
                    {
                        frames[frameIndex, 0, k] = 0;
                        frames[frameIndex, 1, k] = 0;
                        frames[frameIndex, 2, k] = 0;
                        if (tb == 0)
                        {
                            frames[frameIndex, 0, k] = ledBuf[0, srcIndex];
                            frames[frameIndex, 1, k] = ledBuf[1, srcIndex];
                            frames[frameIndex, 2, k] = ledBuf[2, srcIndex];
                            srcIndex++;
                        }
                    }
                    srcIndex = fan * 13;
                    for (int k = fanBase + 13; k < fanBase + 26; k++)
                    {
                        frames[frameIndex, 0, k] = 0;
                        frames[frameIndex, 1, k] = 0;
                        frames[frameIndex, 2, k] = 0;
                        if (tb == 1)
                        {
                            frames[frameIndex, 0, k] = ledBuf[0, srcIndex];
                            frames[frameIndex, 1, k] = ledBuf[1, srcIndex];
                            frames[frameIndex, 2, k] = ledBuf[2, srcIndex];
                            srcIndex++;
                        }
                    }
                }
                frameIndex++;
            }
        }
        return frameIndex;
    }
    internal int TlTwinkle(int tb, int fans, int bright, int direction)
    {
        byte[,] ledBuf = new byte[3, 174];
        byte[] colorSlot = new byte[174]
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
        byte[,] twinkleTable = FlexTwinkleTable.Table;
        if (tb >= 2)
        {
            tb = 0;
        }
        if (fans == 0 || fans > 4)
        {
            fans = 4;
        }
        if (direction > 1)
        {
            direction = 0;
        }
        int totalLeds = fans * 26;
        int frameIndex = 0;
        for (int i = 0; i < 200; i++)
        {
            for (int j = 0; j < totalLeds; j++)
            {
                ledBuf[0, j] = (byte)(palette[tb, colorSlot[j], 0] * twinkleTable[i, j] >> 8);
                ledBuf[1, j] = (byte)(palette[tb, colorSlot[j], 1] * twinkleTable[i, j] >> 8);
                ledBuf[2, j] = (byte)(palette[tb, colorSlot[j], 2] * twinkleTable[i, j] >> 8);
                int frameStart = frameIndex;
                frames[frameIndex, 0, j] = (byte)(ledBuf[0, j] * bright >> 8);
                frames[frameIndex, 1, j] = (byte)(ledBuf[1, j] * bright >> 8);
                frames[frameIndex, 2, j] = (byte)(ledBuf[2, j] * bright >> 8);
            }
            frameIndex++;
        }
        return frameIndex;
    }
}
