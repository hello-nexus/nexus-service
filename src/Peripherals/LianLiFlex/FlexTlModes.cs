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
        byte[,] array = new byte[3, 104];
        byte[,] array2 = new byte[3, 15]
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
        byte[,] array3 = new byte[3, 27]
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
        byte[,] array4 = new byte[3, 39]
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
        byte[,] array5 = new byte[3, 54]
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
        int num = fans * 13;
        int num2 = 0;
        for (int i = 0; i < num; i++)
        {
            int num3 = i;
            for (int j = 0; j < num; j++)
            {
                int num4 = array5[0, num3];
                int num5 = array5[1, num3];
                int num6 = array5[2, num3];
                int num7 = 54;
                if (fans == 1)
                {
                    num4 = array2[0, num3];
                    num5 = array2[1, num3];
                    num6 = array2[2, num3];
                    num7 = 15;
                }
                if (fans == 2)
                {
                    num4 = array3[0, num3];
                    num5 = array3[1, num3];
                    num6 = array3[2, num3];
                    num7 = 27;
                }
                if (fans == 3)
                {
                    num4 = array4[0, num3];
                    num5 = array4[1, num3];
                    num6 = array4[2, num3];
                    num7 = 39;
                }
                int num8 = j;
                if (direction == 0)
                {
                    num8 = num - j - 1;
                }
                array[0, num8] = (byte)(num4 * bright >> 8);
                array[1, num8] = (byte)(num5 * bright >> 8);
                array[2, num8] = (byte)(num6 * bright >> 8);
                num3++;
                if (num3 >= num7)
                {
                    num3 -= num7;
                }
            }
            int num9 = num2;
            for (num3 = 0; num3 < fans; num3++)
            {
                int num8 = num3 * 26;
                int num10 = num3 * 13;
                for (int j = num8; j < num8 + 13; j++)
                {
                    frames[num9, 0, j] = 0;
                    frames[num9, 1, j] = 0;
                    frames[num9, 2, j] = 0;
                    if (tb == 0)
                    {
                        frames[num9, 0, j] = array[0, num10];
                        frames[num9, 1, j] = array[1, num10];
                        frames[num9, 2, j] = array[2, num10];
                        num10++;
                    }
                }
                num10 = num3 * 13;
                for (int j = num8 + 13; j < num8 + 26; j++)
                {
                    frames[num9, 0, j] = 0;
                    frames[num9, 1, j] = 0;
                    frames[num9, 2, j] = 0;
                    if (tb == 1)
                    {
                        frames[num9, 0, j] = array[0, num10];
                        frames[num9, 1, j] = array[1, num10];
                        frames[num9, 2, j] = array[2, num10];
                        num10++;
                    }
                }
            }
            num2++;
        }
        return num2;
    }
    internal int TlRainbowMorph(int tb, int fans, int bright, int direction)
    {
        frames = new byte[255, 3, fans * 26];
        byte[,] array = new byte[3, 104];
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
        int num = fans * 26;
        int num2 = 255;
        int num3 = 0;
        int num4 = 0;
        int num5 = 0;
        for (int i = 0; i < 255; i++)
        {
            for (int j = 0; j < num; j++)
            {
                array[0, j] = (byte)(num2 * bright >> 8);
                array[1, j] = (byte)(num3 * bright >> 8);
                array[2, j] = (byte)(num4 * bright >> 8);
            }
            if (i < 85)
            {
                num2 -= 3;
                num3 += 3;
                num4 = 0;
            }
            else if (i < 170)
            {
                num2 = 0;
                num3 -= 3;
                num4 += 3;
            }
            else
            {
                num2 += 3;
                num3 = 0;
                num4 -= 3;
            }
            int num6 = num5;
            for (int k = 0; k < fans; k++)
            {
                int num7 = k * 26;
                int num8 = k * 13;
                for (int j = num7; j < num7 + 13; j++)
                {
                    frames[num5, 0, j] = 0;
                    frames[num5, 1, j] = 0;
                    frames[num5, 2, j] = 0;
                    if (tb == 0)
                    {
                        frames[num5, 0, j] = array[0, num8];
                        frames[num5, 1, j] = array[1, num8];
                        frames[num5, 2, j] = array[2, num8];
                        num8++;
                    }
                }
                num8 = k * 13;
                for (int j = num7 + 13; j < num7 + 26; j++)
                {
                    frames[num5, 0, j] = 0;
                    frames[num5, 1, j] = 0;
                    frames[num5, 2, j] = 0;
                    if (tb == 1)
                    {
                        frames[num5, 0, j] = array[0, num8];
                        frames[num5, 1, j] = array[1, num8];
                        frames[num5, 2, j] = array[2, num8];
                        num8++;
                    }
                }
            }
            num5++;
        }
        int num9 = num5 / 2;
        byte[,,] array2 = new byte[num9, 3, ledCount];
        for (int l = 0; l < num9; l++)
        {
            for (int m = 0; m < 3; m++)
            {
                for (int n = 0; n < ledCount; n++)
                {
                    array2[l, m, n] = frames[l * 2, m, n];
                }
            }
        }
        frames = array2;
        return num9;
    }
    internal int TlStaticColor(int tb, int fans, int bright, int direction)
    {
        frames = new byte[30, 3, fans * 26];
        byte[,] array = new byte[3, 104];
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
                array[0, j] = (byte)(palette[tb, i, 0] * bright >> 8);
                array[1, j] = (byte)(palette[tb, i, 1] * bright >> 8);
                array[2, j] = (byte)(palette[tb, i, 2] * bright >> 8);
            }
        }
        int num2 = 0;
        for (num2 = 0; num2 < 30; num2++)
        {
            for (int k = 0; k < fans; k++)
            {
                int num3 = k * 26;
                int num4 = k * 13;
                for (int j = num3; j < num3 + 13; j++)
                {
                    frames[num2, 0, j] = 0;
                    frames[num2, 1, j] = 0;
                    frames[num2, 2, j] = 0;
                    if (tb == 0)
                    {
                        frames[num2, 0, j] = array[0, num4];
                        frames[num2, 1, j] = array[1, num4];
                        frames[num2, 2, j] = array[2, num4];
                        num4++;
                    }
                }
                num4 = k * 13;
                for (int j = num3 + 13; j < num3 + 26; j++)
                {
                    frames[num2, 0, j] = 0;
                    frames[num2, 1, j] = 0;
                    frames[num2, 2, j] = 0;
                    if (tb == 1)
                    {
                        frames[num2, 0, j] = array[0, num4];
                        frames[num2, 1, j] = array[1, num4];
                        frames[num2, 2, j] = array[2, num4];
                        num4++;
                    }
                }
            }
        }
        return 30;
    }
    internal int TlBreathing(int tb, int fans, int bright, int direction)
    {
        byte[,] array = new byte[3, 104];
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
        int num = fans * 13;
        int num2 = 0;
        int num3 = 0;
        for (int i = 0; i < 170; i++)
        {
            int num4 = (num3 * 3) & 0xFF;
            for (int j = 0; j < num; j++)
            {
                int num5 = 0;
                if (j >= 13 && j < 26)
                {
                    num5 = 1;
                }
                else if (j >= 26 && j < 39)
                {
                    num5 = 2;
                }
                else if (j >= 39 && j < 54)
                {
                    num5 = 3;
                }
                int num6 = palette[tb, num5, 0] * num4 >> 8;
                int num7 = palette[tb, num5, 1] * num4 >> 8;
                int num8 = palette[tb, num5, 2] * num4 >> 8;
                array[0, j] = (byte)(num6 * bright >> 8);
                array[1, j] = (byte)(num7 * bright >> 8);
                array[2, j] = (byte)(num8 * bright >> 8);
            }
            num3 = ((i >= 85) ? (num3 - 1) : (num3 + 1));
            int num9 = num2;
            for (int k = 0; k < fans; k++)
            {
                int num10 = k * 26;
                int num11 = k * 13;
                for (int j = num10; j < num10 + 13; j++)
                {
                    frames[num9, 0, j] = 0;
                    frames[num9, 1, j] = 0;
                    frames[num9, 2, j] = 0;
                    if (tb == 0)
                    {
                        frames[num9, 0, j] = array[0, num11];
                        frames[num9, 1, j] = array[1, num11];
                        frames[num9, 2, j] = array[2, num11];
                        num11++;
                    }
                }
                num11 = k * 13;
                for (int j = num10 + 13; j < num10 + 26; j++)
                {
                    frames[num9, 0, j] = 0;
                    frames[num9, 1, j] = 0;
                    frames[num9, 2, j] = 0;
                    if (tb == 1)
                    {
                        frames[num9, 0, j] = array[0, num11];
                        frames[num9, 1, j] = array[1, num11];
                        frames[num9, 2, j] = array[2, num11];
                        num11++;
                    }
                }
            }
            num2++;
        }
        return num2;
    }
    internal int TlRunway(int tb, int fans, int bright, int direction)
    {
        byte[,] array = new byte[3, 104];
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
        int num = fans * 13;
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
                    int num4 = palette[tb, num3, 0];
                    int num5 = palette[tb, num3, 1];
                    int num6 = palette[tb, num3, 2];
                    int num7 = k;
                    if (i != 0)
                    {
                        num7 = num - k - 1;
                    }
                    array[0, num7] = (byte)(num4 * bright >> 8);
                    array[1, num7] = (byte)(num5 * bright >> 8);
                    array[2, num7] = (byte)(num6 * bright >> 8);
                }
                int num8 = num2;
                for (int l = 0; l < fans; l++)
                {
                    int num7 = l * 26;
                    int num9 = l * 13;
                    for (int k = num7; k < num7 + 13; k++)
                    {
                        frames[num2, 0, k] = 0;
                        frames[num2, 1, k] = 0;
                        frames[num2, 2, k] = 0;
                        if (tb == 0)
                        {
                            frames[num2, 0, k] = array[0, num9];
                            frames[num2, 1, k] = array[1, num9];
                            frames[num2, 2, k] = array[2, num9];
                            num9++;
                        }
                    }
                    num9 = l * 13;
                    for (int k = num7 + 13; k < num7 + 26; k++)
                    {
                        frames[num2, 0, k] = 0;
                        frames[num2, 1, k] = 0;
                        frames[num2, 2, k] = 0;
                        if (tb == 1)
                        {
                            frames[num2, 0, k] = array[0, num9];
                            frames[num2, 1, k] = array[1, num9];
                            frames[num2, 2, k] = array[2, num9];
                            num9++;
                        }
                    }
                }
                num2++;
            }
        }
        return num2;
    }
    internal int TlMeteor(int tb, int fans, int bright, int direction)
    {
        byte[,] array = new byte[3, 104];
        byte[,] array2 = new byte[4, 8]
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
        int num = fans * 13;
        int num2 = 0;
        for (int i = 0; i < 4; i++)
        {
            for (int j = 0; j < num + fans + fans - 1; j++)
            {
                int num3 = 0;
                for (int k = 0; k < num; k++)
                {
                    int num4 = 0;
                    int num5 = 0;
                    int num6 = 0;
                    if (k <= j && k + fans + fans > j)
                    {
                        num4 = palette[tb, i, 0] * array2[fans - 1, num3] >> 8;
                        num5 = palette[tb, i, 1] * array2[fans - 1, num3] >> 8;
                        num6 = palette[tb, i, 2] * array2[fans - 1, num3] >> 8;
                        num3++;
                    }
                    int num7 = k;
                    if (direction != 0)
                    {
                        num7 = num - k - 1;
                    }
                    array[0, num7] = (byte)(num4 * bright >> 8);
                    array[1, num7] = (byte)(num5 * bright >> 8);
                    array[2, num7] = (byte)(num6 * bright >> 8);
                }
                int num8 = num2;
                for (int l = 0; l < fans; l++)
                {
                    int num7 = l * 26;
                    int num9 = l * 13;
                    for (int k = num7; k < num7 + 13; k++)
                    {
                        frames[num2, 0, k] = 0;
                        frames[num2, 1, k] = 0;
                        frames[num2, 2, k] = 0;
                        if (tb == 0)
                        {
                            frames[num2, 0, k] = array[0, num9];
                            frames[num2, 1, k] = array[1, num9];
                            frames[num2, 2, k] = array[2, num9];
                            num9++;
                        }
                    }
                    num9 = l * 13;
                    for (int k = num7 + 13; k < num7 + 26; k++)
                    {
                        frames[num2, 0, k] = 0;
                        frames[num2, 1, k] = 0;
                        frames[num2, 2, k] = 0;
                        if (tb == 1)
                        {
                            frames[num2, 0, k] = array[0, num9];
                            frames[num2, 1, k] = array[1, num9];
                            frames[num2, 2, k] = array[2, num9];
                            num9++;
                        }
                    }
                }
                num2++;
            }
        }
        return num2;
    }
    internal int TlColorCycle(int tb, int fans, int bright, int direction)
    {
        byte[,,] array = new byte[2, 3, 104];
        byte[] array2 = new byte[4] { 3, 6, 8, 9 };
        byte[] array3 = new byte[4] { 1, 2, 5, 8 };
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
        int num = fans * 13;
        int num2 = 0;
        int num3 = 0;
        for (int i = 0; i < 3; i++)
        {
            num2 = num3;
            num3 += array3[fans - 1];
            for (int j = num2; j < num3; j++)
            {
                array[0, 0, j] = palette[tb, i, 0];
                array[0, 1, j] = palette[tb, i, 1];
                array[0, 2, j] = palette[tb, i, 2];
            }
            num2 = num3;
            num3 += array2[fans - 1];
            for (int j = num2; j < num3; j++)
            {
                array[0, 0, j] = 0;
                array[0, 1, j] = 0;
                array[0, 2, j] = 0;
            }
            if ((tb == 0 && fans == 1) || (tb == 0 && fans == 4))
            {
                num2--;
                num3--;
            }
            if (tb == 0 && fans == 2)
            {
                num2 = num3;
                num3++;
                array[0, 0, num2] = 0;
                array[0, 1, num2] = 0;
                array[0, 2, num2] = 0;
            }
        }
        int num4 = 0;
        for (int k = 0; k < num; k++)
        {
            int num5 = k;
            for (int j = 0; j < num; j++)
            {
                num2 = j;
                if (direction == 0)
                {
                    num2 = num - j - 1;
                }
                array[1, 0, num2] = (byte)(array[0, 0, num5] * bright >> 8);
                array[1, 1, num2] = (byte)(array[0, 1, num5] * bright >> 8);
                array[1, 2, num2] = (byte)(array[0, 2, num5] * bright >> 8);
                num5++;
                if (num5 >= num)
                {
                    num5 = 0;
                }
            }
            int num6 = num4;
            for (num5 = 0; num5 < fans; num5++)
            {
                num2 = num5 * 26;
                num3 = num5 * 13;
                for (int j = num2; j < num2 + 13; j++)
                {
                    frames[num4, 0, j] = 0;
                    frames[num4, 1, j] = 0;
                    frames[num4, 2, j] = 0;
                    if (tb == 0)
                    {
                        frames[num4, 0, j] = array[1, 0, num3];
                        frames[num4, 1, j] = array[1, 1, num3];
                        frames[num4, 2, j] = array[1, 2, num3];
                        num3++;
                    }
                }
                num3 = num5 * 13;
                for (int j = num2 + 13; j < num2 + 26; j++)
                {
                    frames[num4, 0, j] = 0;
                    frames[num4, 1, j] = 0;
                    frames[num4, 2, j] = 0;
                    if (tb == 1)
                    {
                        frames[num4, 0, j] = array[1, 0, num3];
                        frames[num4, 1, j] = array[1, 1, num3];
                        frames[num4, 2, j] = array[1, 2, num3];
                        num3++;
                    }
                }
            }
            num4++;
        }
        return num4;
    }
    internal int TlStack(int tb, int fans, int bright, int direction)
    {
        byte[,] array = new byte[3, 104];
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
        int num = fans * 13;
        int num2 = fans;
        int num3 = 13;
        int num4 = 0;
        for (int i = 0; i < 4; i++)
        {
            num4 = 0;
            int num5 = 0;
            int num6 = 0;
            while (num6 < num3)
            {
                for (int j = 0; j < num - num5; j++)
                {
                    for (int k = 0; k < num - num5; k++)
                    {
                        int num7 = 0;
                        int num8 = 0;
                        int num9 = 0;
                        if (k <= j && k + num2 > j)
                        {
                            num7 = palette[tb, i, 0];
                            num8 = palette[tb, i, 1];
                            num9 = palette[tb, i, 2];
                        }
                        int num10 = k;
                        if (direction != 0)
                        {
                            num10 = num - k - 1;
                        }
                        array[0, num10] = (byte)(num7 * bright >> 8);
                        array[1, num10] = (byte)(num8 * bright >> 8);
                        array[2, num10] = (byte)(num9 * bright >> 8);
                    }
                    int num11 = num4;
                    for (int num10 = 0; num10 < fans; num10++)
                    {
                        int num12 = num10 * 26;
                        int num13 = num10 * 13;
                        for (int k = num12; k < num12 + 13; k++)
                        {
                            frames[num4, 0, k] = 0;
                            frames[num4, 1, k] = 0;
                            frames[num4, 2, k] = 0;
                            if (tb == 0)
                            {
                                frames[num4, 0, k] = array[0, num13];
                                frames[num4, 1, k] = array[1, num13];
                                frames[num4, 2, k] = array[2, num13];
                                num13++;
                            }
                        }
                        num13 = num10 * 13;
                        for (int k = num12 + 13; k < num12 + 26; k++)
                        {
                            frames[num4, 0, k] = 0;
                            frames[num4, 1, k] = 0;
                            frames[num4, 2, k] = 0;
                            if (tb == 1)
                            {
                                frames[num4, 0, k] = array[0, num13];
                                frames[num4, 1, k] = array[1, num13];
                                frames[num4, 2, k] = array[2, num13];
                                num13++;
                            }
                        }
                    }
                    num4++;
                }
                num6++;
                num5 += num2;
            }
            for (int j = 0; j < num; j++)
            {
                for (int k = 0; k < num; k++)
                {
                    int num7 = 0;
                    int num8 = 0;
                    int num9 = 0;
                    if (k > j)
                    {
                        num7 = palette[tb, i, 0];
                        num8 = palette[tb, i, 1];
                        num9 = palette[tb, i, 2];
                    }
                    int num12 = k;
                    if (direction != 0)
                    {
                        num12 = num - k - 1;
                    }
                    array[0, num12] = (byte)(num7 * bright >> 8);
                    array[1, num12] = (byte)(num8 * bright >> 8);
                    array[2, num12] = (byte)(num9 * bright >> 8);
                }
                int num11 = num4;
                for (int num10 = 0; num10 < fans; num10++)
                {
                    int num12 = num10 * 26;
                    int num13 = num10 * 13;
                    for (int k = num12; k < num12 + 13; k++)
                    {
                        frames[num4, 0, k] = 0;
                        frames[num4, 1, k] = 0;
                        frames[num4, 2, k] = 0;
                        if (tb == 0)
                        {
                            frames[num4, 0, k] = array[0, num13];
                            frames[num4, 1, k] = array[1, num13];
                            frames[num4, 2, k] = array[2, num13];
                            num13++;
                        }
                    }
                    num13 = num10 * 13;
                    for (int k = num12 + 13; k < num12 + 26; k++)
                    {
                        frames[num4, 0, k] = 0;
                        frames[num4, 1, k] = 0;
                        frames[num4, 2, k] = 0;
                        if (tb == 1)
                        {
                            frames[num4, 0, k] = array[0, num13];
                            frames[num4, 1, k] = array[1, num13];
                            frames[num4, 2, k] = array[2, num13];
                            num13++;
                        }
                    }
                }
                num4++;
            }
        }
        return num4;
    }
    internal int TlTwinkle(int tb, int fans, int bright, int direction)
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
        int num = fans * 26;
        int num2 = 0;
        for (int i = 0; i < 200; i++)
        {
            for (int j = 0; j < num; j++)
            {
                array[0, j] = (byte)(palette[tb, array2[j], 0] * twinkleArr[i, j] >> 8);
                array[1, j] = (byte)(palette[tb, array2[j], 1] * twinkleArr[i, j] >> 8);
                array[2, j] = (byte)(palette[tb, array2[j], 2] * twinkleArr[i, j] >> 8);
                int num3 = num2;
                frames[num2, 0, j] = (byte)(array[0, j] * bright >> 8);
                frames[num2, 1, j] = (byte)(array[1, j] * bright >> 8);
                frames[num2, 2, j] = (byte)(array[2, j] * bright >> 8);
            }
            num2++;
        }
        return num2;
    }
}
