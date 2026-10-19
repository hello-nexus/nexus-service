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

    private void CopyColor(byte[,,] colors, int p1, int p2, int d)
    {
        for (var i = 0; i < 3; i++)
        {
            colors[p1, p2, i] = palette[0, d, i];
        }
    }

    private const int PowerCap = FlexPalette.PowerCap;

    private static (byte, byte, byte) GetRgb(byte r, byte g, byte b) => FlexPalette.Cap(r, g, b);

    internal int InfRainbow(int ie, int fans, int bright, int direction, int pn)
    {
        byte[,] array = new byte[3, 176];
        byte[] array2 = new byte[4] { 12, 6, 4, 3 };
        byte[,] array3 = new byte[3, 4]
        {
            { 8, 16, 24, 32 },
            { 10, 20, 30, 40 },
            { 8, 16, 24, 32 }
        };
        byte[] array4 = new byte[32]
        {
            3, 4, 2, 5, 1, 6, 8, 7, 11, 12,
            10, 13, 9, 14, 16, 15, 19, 20, 18, 21,
            17, 22, 24, 23, 27, 28, 26, 29, 25, 30,
            32, 31
        };
        byte[] array5 = new byte[32]
        {
            7, 8, 6, 1, 5, 2, 4, 3, 15, 16,
            14, 9, 13, 10, 12, 11, 23, 24, 22, 17,
            21, 18, 20, 19, 31, 32, 30, 25, 29, 26,
            28, 27
        };
        byte[,] array6 = new byte[3, 120]
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
        byte[,] array7 = new byte[3, 96]
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
        if (ie > 2)
        {
            ie = 0;
        }
        if (fans == 0 || fans > 4)
        {
            fans = 4;
        }
        if (direction > 1)
        {
            direction = 0;
        }
        if (pn > 1)
        {
            pn = 0;
        }
        byte b = array3[ie, fans - 1];
        byte b2 = array2[fans - 1];
        int num = 0;
        for (int i = 0; i < b; i++)
        {
            int num2 = i * b2;
            for (int j = 0; j < b; j++)
            {
                int num3 = j;
                if (direction == 0)
                {
                    num3 = b - j - 1;
                }
                switch (ie)
                {
                case 1:
                    array[0, num3] = (byte)(array6[0, num2] * bright >> 8);
                    array[1, num3] = (byte)(array6[1, num2] * bright >> 8);
                    array[2, num3] = (byte)(array6[2, num2] * bright >> 8);
                    num2 += b2;
                    if (num2 >= 120)
                    {
                        num2 -= 120;
                    }
                    continue;
                case 2:
                    if (pn == 0)
                    {
                        num3 = array4[num3] - 1;
                    }
                    break;
                }
                if (ie == 2 && pn == 1)
                {
                    num3 = array5[num3] - 1;
                }
                array[0, num3] = (byte)(array7[0, num2] * bright >> 8);
                array[1, num3] = (byte)(array7[1, num2] * bright >> 8);
                array[2, num3] = (byte)(array7[2, num2] * bright >> 8);
                num2 += b2;
                if (num2 >= 96)
                {
                    num2 -= 96;
                }
            }
            int num4 = num;
            for (num2 = 0; num2 < fans; num2++)
            {
                int num5 = num2 * 44;
                int num3 = num2 * 8;
                for (int j = num5; j < num5 + 8; j++)
                {
                    frames[num4, 0, j] = 0;
                    frames[num4, 1, j] = 0;
                    frames[num4, 2, j] = 0;
                    if (ie == 2)
                    {
                        frames[num4, 0, j] = array[0, num3];
                        frames[num4, 1, j] = array[1, num3];
                        frames[num4, 2, j] = array[2, num3];
                        num3++;
                    }
                }
                num3 = num2 * 10;
                if (pn == 0)
                {
                    num3 = num3 + 10 - 1;
                }
                for (int j = num5 + 8; j < num5 + 18; j++)
                {
                    frames[num4, 0, j] = 0;
                    frames[num4, 1, j] = 0;
                    frames[num4, 2, j] = 0;
                    if (ie == 1)
                    {
                        frames[num4, 0, j] = array[0, num3];
                        frames[num4, 1, j] = array[1, num3];
                        frames[num4, 2, j] = array[2, num3];
                        num3 = ((pn != 1) ? (num3 - 1) : (num3 + 1));
                    }
                }
                num3 = num2 * 8;
                if (pn == 0)
                {
                    num3 = num3 + 8 - 1;
                }
                for (int j = num5 + 18; j < num5 + 26; j++)
                {
                    frames[num4, 0, j] = 0;
                    frames[num4, 1, j] = 0;
                    frames[num4, 2, j] = 0;
                    if (ie == 0)
                    {
                        frames[num4, 0, j] = array[0, num3];
                        frames[num4, 1, j] = array[1, num3];
                        frames[num4, 2, j] = array[2, num3];
                        num3 = ((pn != 1) ? (num3 - 1) : (num3 + 1));
                    }
                }
                num3 = num2 * 10;
                if (pn == 0)
                {
                    num3 = num3 + 10 - 1;
                }
                for (int j = num5 + 26; j < num5 + 36; j++)
                {
                    frames[num4, 0, j] = 0;
                    frames[num4, 1, j] = 0;
                    frames[num4, 2, j] = 0;
                    if (ie == 1)
                    {
                        frames[num4, 0, j] = array[0, num3];
                        frames[num4, 1, j] = array[1, num3];
                        frames[num4, 2, j] = array[2, num3];
                        num3 = ((pn != 1) ? (num3 - 1) : (num3 + 1));
                    }
                }
                num3 = num2 * 8;
                if (pn == 0)
                {
                    num3 = num3 + 8 - 1;
                }
                for (int j = num5 + 36; j < num5 + 44; j++)
                {
                    frames[num4, 0, j] = 0;
                    frames[num4, 1, j] = 0;
                    frames[num4, 2, j] = 0;
                    if (ie == 0)
                    {
                        frames[num4, 0, j] = array[0, num3];
                        frames[num4, 1, j] = array[1, num3];
                        frames[num4, 2, j] = array[2, num3];
                        num3 = ((pn != 1) ? (num3 - 1) : (num3 + 1));
                    }
                }
            }
            num++;
        }
        return num;
    }
    internal int InfRainbowMorph(int ie, int fans, int bright, int direction, int pn)
    {
        byte b = byte.MaxValue;
        byte b2 = 0;
        byte b3 = 0;
        byte[,] array = new byte[3, 176];
        byte[,] array2 = new byte[3, 4]
        {
            { 8, 16, 24, 32 },
            { 10, 20, 30, 40 },
            { 8, 16, 24, 32 }
        };
        if (ie > 2)
        {
            ie = 0;
        }
        if (fans == 0 || fans > 4)
        {
            fans = 4;
        }
        if (direction > 1)
        {
            direction = 0;
        }
        if (pn > 1)
        {
            pn = 0;
        }
        int num = array2[ie, fans - 1];
        for (int i = 0; i < 255; i++)
        {
            for (int j = 0; j < num; j++)
            {
                array[0, j] = (byte)(b * (byte)bright >> 8);
                array[1, j] = (byte)(b2 * (byte)bright >> 8);
                array[2, j] = (byte)(b3 * (byte)bright >> 8);
            }
            if (i < 85)
            {
                b -= 3;
                b2 += 3;
                b3 = 0;
            }
            else if (i < 170)
            {
                b = 0;
                b2 -= 3;
                b3 += 3;
            }
            else
            {
                b += 3;
                b2 = 0;
                b3 -= 3;
            }
            int num2 = i;
            for (int k = 0; k < fans; k++)
            {
                int num3 = k * 44;
                int num4 = k * 8;
                for (int j = num3; j < num3 + 8; j++)
                {
                    frames[num2, 0, j] = 0;
                    frames[num2, 1, j] = 0;
                    frames[num2, 2, j] = 0;
                    if (ie == 2)
                    {
                        frames[num2, 0, j] = array[0, num4];
                        frames[num2, 1, j] = array[1, num4];
                        frames[num2, 2, j] = array[2, num4];
                        num4++;
                    }
                }
                num4 = k * 10;
                for (int j = num3 + 8; j < num3 + 18; j++)
                {
                    frames[num2, 0, j] = 0;
                    frames[num2, 1, j] = 0;
                    frames[num2, 2, j] = 0;
                    if (ie == 1)
                    {
                        frames[num2, 0, j] = array[0, num4];
                        frames[num2, 1, j] = array[1, num4];
                        frames[num2, 2, j] = array[2, num4];
                        num4++;
                    }
                }
                num4 = k * 8;
                for (int j = num3 + 18; j < num3 + 26; j++)
                {
                    frames[num2, 0, j] = 0;
                    frames[num2, 1, j] = 0;
                    frames[num2, 2, j] = 0;
                    if (ie == 0)
                    {
                        frames[num2, 0, j] = array[0, num4];
                        frames[num2, 1, j] = array[1, num4];
                        frames[num2, 2, j] = array[2, num4];
                        num4++;
                    }
                }
                num4 = k * 10;
                for (int j = num3 + 26; j < num3 + 36; j++)
                {
                    frames[num2, 0, j] = 0;
                    frames[num2, 1, j] = 0;
                    frames[num2, 2, j] = 0;
                    if (ie == 1)
                    {
                        frames[num2, 0, j] = array[0, num4];
                        frames[num2, 1, j] = array[1, num4];
                        frames[num2, 2, j] = array[2, num4];
                        num4++;
                    }
                }
                num4 = k * 8;
                for (int j = num3 + 36; j < num3 + 44; j++)
                {
                    frames[num2, 0, j] = 0;
                    frames[num2, 1, j] = 0;
                    frames[num2, 2, j] = 0;
                    if (ie == 0)
                    {
                        frames[num2, 0, j] = array[0, num4];
                        frames[num2, 1, j] = array[1, num4];
                        frames[num2, 2, j] = array[2, num4];
                        num4++;
                    }
                }
            }
        }
        int num5 = 127;
        byte[,,] array3 = new byte[num5, 3, ledCount];
        for (int l = 0; l < num5; l++)
        {
            for (int m = 0; m < 3; m++)
            {
                for (int n = 0; n < ledCount; n++)
                {
                    array3[l, m, n] = frames[l * 2, m, n];
                }
            }
        }
        frames = array3;
        return num5;
    }
    internal int InfStaticColor(int ie, int fans, int bright, int direction, int pn)
    {
        frames = new byte[30, 3, fans * 44];
        byte[,] array = new byte[3, 176];
        byte[,] array2 = new byte[3, 4]
        {
            { 8, 16, 24, 32 },
            { 10, 20, 30, 40 },
            { 8, 16, 24, 32 }
        };
        if (ie > 2)
        {
            ie = 0;
        }
        if (fans == 0 || fans > 4)
        {
            fans = 4;
        }
        if (direction > 1)
        {
            direction = 0;
        }
        if (pn > 1)
        {
            pn = 0;
        }
        int num = array2[ie, 0];
        for (byte b = 0; b < fans; b++)
        {
            for (int i = b * num; i < (b + 1) * num; i++)
            {
                array[0, i] = (byte)(palette[ie, b, 0] * (byte)bright >> 8);
                array[1, i] = (byte)(palette[ie, b, 1] * (byte)bright >> 8);
                array[2, i] = (byte)(palette[ie, b, 2] * (byte)bright >> 8);
            }
        }
        int num2 = 0;
        for (num2 = 0; num2 < 30; num2++)
        {
            for (int j = 0; j < fans; j++)
            {
                int num3 = j * 44;
                int num4 = j * 8;
                for (int i = num3; i < num3 + 8; i++)
                {
                    frames[num2, 0, i] = 0;
                    frames[num2, 1, i] = 0;
                    frames[num2, 2, i] = 0;
                    if (ie == 2)
                    {
                        frames[num2, 0, i] = array[0, num4];
                        frames[num2, 1, i] = array[1, num4];
                        frames[num2, 2, i] = array[2, num4];
                        num4++;
                    }
                }
                num4 = j * 10;
                for (int i = num3 + 8; i < num3 + 18; i++)
                {
                    frames[num2, 0, i] = 0;
                    frames[num2, 1, i] = 0;
                    frames[num2, 2, i] = 0;
                    if (ie == 1)
                    {
                        frames[num2, 0, i] = array[0, num4];
                        frames[num2, 1, i] = array[1, num4];
                        frames[num2, 2, i] = array[2, num4];
                        num4++;
                    }
                }
                num4 = j * 8;
                for (int i = num3 + 18; i < num3 + 26; i++)
                {
                    frames[num2, 0, i] = 0;
                    frames[num2, 1, i] = 0;
                    frames[num2, 2, i] = 0;
                    if (ie == 0)
                    {
                        frames[num2, 0, i] = array[0, num4];
                        frames[num2, 1, i] = array[1, num4];
                        frames[num2, 2, i] = array[2, num4];
                        num4++;
                    }
                }
                num4 = j * 10;
                for (int i = num3 + 26; i < num3 + 36; i++)
                {
                    frames[num2, 0, i] = 0;
                    frames[num2, 1, i] = 0;
                    frames[num2, 2, i] = 0;
                    if (ie == 1)
                    {
                        frames[num2, 0, i] = array[0, num4];
                        frames[num2, 1, i] = array[1, num4];
                        frames[num2, 2, i] = array[2, num4];
                        num4++;
                    }
                }
                num4 = j * 8;
                for (int i = num3 + 36; i < num3 + 44; i++)
                {
                    frames[num2, 0, i] = 0;
                    frames[num2, 1, i] = 0;
                    frames[num2, 2, i] = 0;
                    if (ie == 0)
                    {
                        frames[num2, 0, i] = array[0, num4];
                        frames[num2, 1, i] = array[1, num4];
                        frames[num2, 2, i] = array[2, num4];
                        num4++;
                    }
                }
            }
        }
        return 30;
    }
    internal int InfBreathing(int ie, int fans, int bright, int direction, int pn)
    {
        byte[,] array = new byte[3, 176];
        byte[,] array2 = new byte[3, 4]
        {
            { 8, 16, 24, 32 },
            { 10, 20, 30, 40 },
            { 8, 16, 24, 32 }
        };
        if (ie > 2)
        {
            ie = 0;
        }
        if (fans == 0 || fans > 4)
        {
            fans = 4;
        }
        if (direction > 1)
        {
            direction = 0;
        }
        if (pn > 1)
        {
            pn = 0;
        }
        int num = array2[ie, fans - 1];
        int num2 = 0;
        for (int i = 0; i < 170; i++)
        {
            byte b = (byte)((num2 * 3) & 0xFF);
            for (int j = 0; j < num; j++)
            {
                int num3 = 0;
                if (j >= array2[ie, 0] && j < array2[ie, 1])
                {
                    num3 = 1;
                }
                else if (j >= array2[ie, 1] && j < array2[ie, 2])
                {
                    num3 = 2;
                }
                else if (j >= array2[ie, 2] && j < array2[ie, 3])
                {
                    num3 = 3;
                }
                byte b2 = (byte)(palette[ie, num3, 0] * b >> 8);
                byte b3 = (byte)(palette[ie, num3, 1] * b >> 8);
                byte b4 = (byte)(palette[ie, num3, 2] * b >> 8);
                array[0, j] = (byte)(b2 * (byte)bright >> 8);
                array[1, j] = (byte)(b3 * (byte)bright >> 8);
                array[2, j] = (byte)(b4 * (byte)bright >> 8);
            }
            num2 = ((i >= 85) ? (num2 - 1) : (num2 + 1));
            int num4 = i;
            for (int k = 0; k < fans; k++)
            {
                int num5 = k * 44;
                int num6 = k * 8;
                for (int j = num5; j < num5 + 8; j++)
                {
                    frames[num4, 0, j] = 0;
                    frames[num4, 1, j] = 0;
                    frames[num4, 2, j] = 0;
                    if (ie == 2)
                    {
                        frames[num4, 0, j] = array[0, num6];
                        frames[num4, 1, j] = array[1, num6];
                        frames[num4, 2, j] = array[2, num6];
                        num6++;
                    }
                }
                num6 = k * 10;
                for (int j = num5 + 8; j < num5 + 18; j++)
                {
                    frames[num4, 0, j] = 0;
                    frames[num4, 1, j] = 0;
                    frames[num4, 2, j] = 0;
                    if (ie == 1)
                    {
                        frames[num4, 0, j] = array[0, num6];
                        frames[num4, 1, j] = array[1, num6];
                        frames[num4, 2, j] = array[2, num6];
                        num6++;
                    }
                }
                num6 = k * 8;
                for (int j = num5 + 18; j < num5 + 26; j++)
                {
                    frames[num4, 0, j] = 0;
                    frames[num4, 1, j] = 0;
                    frames[num4, 2, j] = 0;
                    if (ie == 0)
                    {
                        frames[num4, 0, j] = array[0, num6];
                        frames[num4, 1, j] = array[1, num6];
                        frames[num4, 2, j] = array[2, num6];
                        num6++;
                    }
                }
                num6 = k * 10;
                for (int j = num5 + 26; j < num5 + 36; j++)
                {
                    frames[num4, 0, j] = 0;
                    frames[num4, 1, j] = 0;
                    frames[num4, 2, j] = 0;
                    if (ie == 1)
                    {
                        frames[num4, 0, j] = array[0, num6];
                        frames[num4, 1, j] = array[1, num6];
                        frames[num4, 2, j] = array[2, num6];
                        num6++;
                    }
                }
                num6 = k * 8;
                for (int j = num5 + 36; j < num5 + 44; j++)
                {
                    frames[num4, 0, j] = 0;
                    frames[num4, 1, j] = 0;
                    frames[num4, 2, j] = 0;
                    if (ie == 0)
                    {
                        frames[num4, 0, j] = array[0, num6];
                        frames[num4, 1, j] = array[1, num6];
                        frames[num4, 2, j] = array[2, num6];
                        num6++;
                    }
                }
            }
        }
        return 170;
    }
    internal int InfRunway(int ie, int fans, int bright, int direction, int pn)
    {
        byte[,] array = new byte[3, 176];
        byte[,,] array2 = new byte[3, 1, 4]
        {
            { { 8, 16, 24, 32 } },
            { { 10, 20, 30, 40 } },
            { { 8, 16, 24, 32 } }
        };
        if (ie > 2)
        {
            ie = 0;
        }
        if (fans == 0 || fans > 4)
        {
            fans = 4;
        }
        if (direction > 1)
        {
            direction = 0;
        }
        if (pn > 1)
        {
            pn = 0;
        }
        pn = ((pn == 0) ? 1 : 0);
        int num = array2[ie, 0, fans - 1];
        int num2 = 0;
        for (int i = 0; i < 2; i++)
        {
            for (int j = 0; j < num + fans + fans - 1; j++)
            {
                for (int k = 0; k < num; k++)
                {
                    int num3 = 1;
                    if (k <= j && k + fans + fans > j)
                    {
                        num3 = 0;
                    }
                    byte b = palette[ie, num3, 0];
                    byte b2 = palette[ie, num3, 1];
                    byte b3 = palette[ie, num3, 2];
                    int num4 = k;
                    if (i == 1)
                    {
                        num4 = num - k - 1;
                    }
                    array[0, num4] = (byte)(b * (byte)bright >> 8);
                    array[1, num4] = (byte)(b2 * (byte)bright >> 8);
                    array[2, num4] = (byte)(b3 * (byte)bright >> 8);
                }
                int num5 = num2;
                for (int l = 0; l < fans; l++)
                {
                    int num6 = l * 44;
                    int num4 = l * 8;
                    if (pn == 1)
                    {
                        num4 = num4 + 8 - 1;
                    }
                    for (int k = num6; k < num6 + 8; k++)
                    {
                        frames[num5, 0, k] = 0;
                        frames[num5, 1, k] = 0;
                        frames[num5, 2, k] = 0;
                        if (ie == 2)
                        {
                            frames[num5, 0, k] = array[0, num4];
                            frames[num5, 1, k] = array[1, num4];
                            frames[num5, 2, k] = array[2, num4];
                            num4 = ((pn != 0) ? (num4 - 1) : (num4 + 1));
                        }
                    }
                    num4 = l * 10;
                    if (pn == 1)
                    {
                        num4 = num4 + 10 - 1;
                    }
                    for (int k = num6 + 8; k < num6 + 18; k++)
                    {
                        frames[num5, 0, k] = 0;
                        frames[num5, 1, k] = 0;
                        frames[num5, 2, k] = 0;
                        if (ie == 1)
                        {
                            frames[num5, 0, k] = array[0, num4];
                            frames[num5, 1, k] = array[1, num4];
                            frames[num5, 2, k] = array[2, num4];
                            num4 = ((pn != 0) ? (num4 - 1) : (num4 + 1));
                        }
                    }
                    num4 = l * 8;
                    if (pn == 1)
                    {
                        num4 = num4 + 8 - 1;
                    }
                    for (int k = num6 + 18; k < num6 + 26; k++)
                    {
                        frames[num5, 0, k] = 0;
                        frames[num5, 1, k] = 0;
                        frames[num5, 2, k] = 0;
                        if (ie == 0)
                        {
                            frames[num5, 0, k] = array[0, num4];
                            frames[num5, 1, k] = array[1, num4];
                            frames[num5, 2, k] = array[2, num4];
                            num4 = ((pn != 0) ? (num4 - 1) : (num4 + 1));
                        }
                    }
                    num4 = l * 10;
                    if (pn == 1)
                    {
                        num4 = num4 + 10 - 1;
                    }
                    for (int k = num6 + 26; k < num6 + 36; k++)
                    {
                        frames[num5, 0, k] = 0;
                        frames[num5, 1, k] = 0;
                        frames[num5, 2, k] = 0;
                        if (ie == 1)
                        {
                            frames[num5, 0, k] = array[0, num4];
                            frames[num5, 1, k] = array[1, num4];
                            frames[num5, 2, k] = array[2, num4];
                            num4 = ((pn != 0) ? (num4 - 1) : (num4 + 1));
                        }
                    }
                    num4 = l * 8;
                    if (pn == 1)
                    {
                        num4 = num4 + 8 - 1;
                    }
                    for (int k = num6 + 36; k < num6 + 44; k++)
                    {
                        frames[num5, 0, k] = 0;
                        frames[num5, 1, k] = 0;
                        frames[num5, 2, k] = 0;
                        if (ie == 0)
                        {
                            frames[num5, 0, k] = array[0, num4];
                            frames[num5, 1, k] = array[1, num4];
                            frames[num5, 2, k] = array[2, num4];
                            num4 = ((pn != 0) ? (num4 - 1) : (num4 + 1));
                        }
                    }
                }
                num2++;
            }
        }
        return num2;
    }
    internal int InfMeteor(int ie, int fans, int bright, int direction, int pn)
    {
        byte[,] array = new byte[3, 176];
        byte[,,] array2 = new byte[3, 1, 4]
        {
            { { 8, 16, 24, 32 } },
            { { 10, 20, 30, 40 } },
            { { 8, 16, 24, 32 } }
        };
        byte[,,] array3 = new byte[4, 1, 8]
        {
            { { 32, 255, 0, 0, 0, 0, 0, 0 } },
            { { 16, 64, 128, 255, 0, 0, 0, 0 } },
            { { 8, 16, 32, 64, 128, 255, 0, 0 } },
            { { 6, 10, 16, 32, 64, 96, 168, 255 } }
        };
        if (ie > 2)
        {
            ie = 0;
        }
        if (fans == 0 || fans > 4)
        {
            fans = 4;
        }
        if (direction > 1)
        {
            direction = 0;
        }
        if (pn > 1)
        {
            pn = 0;
        }
        pn = ((pn == 0) ? 1 : 0);
        int num = array2[ie, 0, fans - 1];
        int num2 = 0;
        for (int i = 0; i < 4; i++)
        {
            for (int j = 0; j < num + fans + fans - 1; j++)
            {
                int num3 = 0;
                for (int k = 0; k < num; k++)
                {
                    byte b = 0;
                    byte b2 = 0;
                    byte b3 = 0;
                    if (k <= j && k + fans + fans > j)
                    {
                        b = (byte)(palette[ie, i, 0] * array3[fans - 1, 0, num3] >> 8);
                        b2 = (byte)(palette[ie, i, 1] * array3[fans - 1, 0, num3] >> 8);
                        b3 = (byte)(palette[ie, i, 2] * array3[fans - 1, 0, num3] >> 8);
                        num3++;
                    }
                    int num4 = k;
                    if (direction == 1)
                    {
                        num4 = num - k - 1;
                    }
                    array[0, num4] = (byte)(b * (byte)bright >> 8);
                    array[1, num4] = (byte)(b2 * (byte)bright >> 8);
                    array[2, num4] = (byte)(b3 * (byte)bright >> 8);
                }
                int num5 = num2;
                for (int l = 0; l < fans; l++)
                {
                    int num6 = l * 44;
                    int num4 = l * 8;
                    if (pn == 1)
                    {
                        num4 = num4 + 8 - 1;
                    }
                    for (int k = num6; k < num6 + 8; k++)
                    {
                        frames[num5, 0, k] = 0;
                        frames[num5, 1, k] = 0;
                        frames[num5, 2, k] = 0;
                        if (ie == 2)
                        {
                            frames[num5, 0, k] = array[0, num4];
                            frames[num5, 1, k] = array[1, num4];
                            frames[num5, 2, k] = array[2, num4];
                            num4 = ((pn != 0) ? (num4 - 1) : (num4 + 1));
                        }
                    }
                    num4 = l * 10;
                    if (pn == 1)
                    {
                        num4 = num4 + 10 - 1;
                    }
                    for (int k = num6 + 8; k < num6 + 18; k++)
                    {
                        frames[num5, 0, k] = 0;
                        frames[num5, 1, k] = 0;
                        frames[num5, 2, k] = 0;
                        if (ie == 1)
                        {
                            frames[num5, 0, k] = array[0, num4];
                            frames[num5, 1, k] = array[1, num4];
                            frames[num5, 2, k] = array[2, num4];
                            num4 = ((pn != 0) ? (num4 - 1) : (num4 + 1));
                        }
                    }
                    num4 = l * 8;
                    if (pn == 1)
                    {
                        num4 = num4 + 8 - 1;
                    }
                    for (int k = num6 + 18; k < num6 + 26; k++)
                    {
                        frames[num5, 0, k] = 0;
                        frames[num5, 1, k] = 0;
                        frames[num5, 2, k] = 0;
                        if (ie == 0)
                        {
                            frames[num5, 0, k] = array[0, num4];
                            frames[num5, 1, k] = array[1, num4];
                            frames[num5, 2, k] = array[2, num4];
                            num4 = ((pn != 0) ? (num4 - 1) : (num4 + 1));
                        }
                    }
                    num4 = l * 10;
                    if (pn == 1)
                    {
                        num4 = num4 + 10 - 1;
                    }
                    for (int k = num6 + 26; k < num6 + 36; k++)
                    {
                        frames[num5, 0, k] = 0;
                        frames[num5, 1, k] = 0;
                        frames[num5, 2, k] = 0;
                        if (ie == 1)
                        {
                            frames[num5, 0, k] = array[0, num4];
                            frames[num5, 1, k] = array[1, num4];
                            frames[num5, 2, k] = array[2, num4];
                            num4 = ((pn != 0) ? (num4 - 1) : (num4 + 1));
                        }
                    }
                    num4 = l * 8;
                    if (pn == 1)
                    {
                        num4 = num4 + 8 - 1;
                    }
                    for (int k = num6 + 36; k < num6 + 44; k++)
                    {
                        frames[num5, 0, k] = 0;
                        frames[num5, 1, k] = 0;
                        frames[num5, 2, k] = 0;
                        if (ie == 0)
                        {
                            frames[num5, 0, k] = array[0, num4];
                            frames[num5, 1, k] = array[1, num4];
                            frames[num5, 2, k] = array[2, num4];
                            num4 = ((pn != 0) ? (num4 - 1) : (num4 + 1));
                        }
                    }
                }
                num2++;
            }
        }
        return num2;
    }
    internal int InfTwinkle(int ie, int fans, int bright, int direction, int pn)
    {
        byte[,] array = new byte[3, 174];
        byte[] array2 = new byte[174]
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
        byte[,] twinkleArr = FlexTwinkleTable.Table;
        if (ie > 2)
        {
            ie = 0;
        }
        if (fans == 0 || fans > 4)
        {
            fans = 4;
        }
        if (direction > 1)
        {
            direction = 0;
        }
        if (pn > 1)
        {
            pn = 0;
        }
        int i;
        for (i = 0; i < 200; i++)
        {
            for (int j = 0; j < 174 && j < fans * 44; j++)
            {
                array[0, j] = (byte)(palette[2, array2[j], 0] * twinkleArr[i, j] >> 8);
                array[1, j] = (byte)(palette[2, array2[j], 1] * twinkleArr[i, j] >> 8);
                array[2, j] = (byte)(palette[2, array2[j], 2] * twinkleArr[i, j] >> 8);
                int num = i;
                frames[num, 0, j] = (byte)(array[0, j] * (byte)bright >> 8);
                frames[num, 1, j] = (byte)(array[1, j] * (byte)bright >> 8);
                frames[num, 2, j] = (byte)(array[2, j] * (byte)bright >> 8);
            }
        }
        return i;
    }
    internal int InfTaichi(int ie, int fans, int bright, int direction, int pn)
    {
        byte[,] array = new byte[3, 176];
        byte[] array2 = new byte[8] { 0, 0, 0, 0, 1, 1, 1, 1 };
        byte[] array3 = new byte[10] { 0, 0, 0, 0, 0, 1, 1, 1, 1, 1 };
        if (ie > 2)
        {
            ie = 0;
        }
        if (fans == 0 || fans > 4)
        {
            fans = 4;
        }
        if (direction > 1)
        {
            direction = 0;
        }
        if (pn > 1)
        {
            pn = 0;
        }
        pn = ((pn == 0) ? 1 : 0);
        int num = 8;
        if (ie == 1)
        {
            num = 10;
        }
        int num2 = 0;
        for (int i = 0; i < num; i++)
        {
            for (int j = 0; j < num; j++)
            {
                int num3 = i + j;
                if (num3 >= num)
                {
                    num3 -= num;
                }
                byte b = ((ie != 1) ? array2[num3] : array3[num3]);
                byte b2 = (byte)(palette[ie, b, 0] * (byte)bright >> 8);
                byte b3 = (byte)(palette[ie, b, 1] * (byte)bright >> 8);
                byte b4 = (byte)(palette[ie, b, 2] * (byte)bright >> 8);
                int num4 = j;
                if (direction == 1)
                {
                    num4 = num - j - 1;
                }
                array[0, num4] = b2;
                array[1, num4] = b3;
                array[2, num4] = b4;
            }
            for (byte b5 = 1; b5 < fans; b5++)
            {
                for (int j = 0; j < num; j++)
                {
                    array[0, b5 * num + j] = array[0, j];
                    array[1, b5 * num + j] = array[1, j];
                    array[2, b5 * num + j] = array[2, j];
                }
            }
            int num5 = num2;
            for (int num3 = 0; num3 < fans; num3++)
            {
                int num6 = num3 * 44;
                int num4 = num3 * 8;
                if (pn == 1)
                {
                    num4 = num4 + 8 - 1;
                }
                for (int j = num6; j < num6 + 8; j++)
                {
                    frames[num5, 0, j] = 0;
                    frames[num5, 1, j] = 0;
                    frames[num5, 2, j] = 0;
                    if (ie == 2)
                    {
                        frames[num5, 0, j] = array[0, num4];
                        frames[num5, 1, j] = array[1, num4];
                        frames[num5, 2, j] = array[2, num4];
                        num4 = ((pn != 0) ? (num4 - 1) : (num4 + 1));
                    }
                }
                num4 = num3 * 10;
                if (pn == 1)
                {
                    num4 = num4 + 10 - 1;
                }
                for (int j = num6 + 8; j < num6 + 18; j++)
                {
                    frames[num5, 0, j] = 0;
                    frames[num5, 1, j] = 0;
                    frames[num5, 2, j] = 0;
                    if (ie == 1)
                    {
                        frames[num5, 0, j] = array[0, num4];
                        frames[num5, 1, j] = array[1, num4];
                        frames[num5, 2, j] = array[2, num4];
                        num4 = ((pn != 0) ? (num4 - 1) : (num4 + 1));
                    }
                }
                num4 = num3 * 8;
                if (pn == 1)
                {
                    num4 = num4 + 8 - 1;
                }
                for (int j = num6 + 18; j < num6 + 26; j++)
                {
                    frames[num5, 0, j] = 0;
                    frames[num5, 1, j] = 0;
                    frames[num5, 2, j] = 0;
                    if (ie == 0)
                    {
                        frames[num5, 0, j] = array[0, num4];
                        frames[num5, 1, j] = array[1, num4];
                        frames[num5, 2, j] = array[2, num4];
                        num4 = ((pn != 0) ? (num4 - 1) : (num4 + 1));
                    }
                }
                num4 = num3 * 10;
                if (pn == 1)
                {
                    num4 = num4 + 10 - 1;
                }
                for (int j = num6 + 26; j < num6 + 36; j++)
                {
                    frames[num5, 0, j] = 0;
                    frames[num5, 1, j] = 0;
                    frames[num5, 2, j] = 0;
                    if (ie == 1)
                    {
                        frames[num5, 0, j] = array[0, num4];
                        frames[num5, 1, j] = array[1, num4];
                        frames[num5, 2, j] = array[2, num4];
                        num4 = ((pn != 0) ? (num4 - 1) : (num4 + 1));
                    }
                }
                num4 = num3 * 8;
                if (pn == 1)
                {
                    num4 = num4 + 8 - 1;
                }
                for (int j = num6 + 36; j < num6 + 44; j++)
                {
                    frames[num5, 0, j] = 0;
                    frames[num5, 1, j] = 0;
                    frames[num5, 2, j] = 0;
                    if (ie == 0)
                    {
                        frames[num5, 0, j] = array[0, num4];
                        frames[num5, 1, j] = array[1, num4];
                        frames[num5, 2, j] = array[2, num4];
                        num4 = ((pn != 0) ? (num4 - 1) : (num4 + 1));
                    }
                }
            }
            num2++;
        }
        return num2;
    }
}
