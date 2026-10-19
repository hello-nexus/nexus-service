using System;

namespace Nexus.Service.Peripherals.LianLiFlex;

internal sealed partial class FlexSlInfModes
{
    internal int InfMopUp(int ie, int fans, int bright, int direction, int pn)
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
        for (int i = 0; i < 4; i++)
        {
            for (int j = 0; j < num + fans + fans; j++)
            {
                for (int k = 0; k < num; k++)
                {
                    byte b = 0;
                    byte b2 = 0;
                    byte b3 = 0;
                    if (k <= j && k + fans + fans > j)
                    {
                        b = palette[ie, i, 0];
                        b2 = palette[ie, i, 1];
                        b3 = palette[ie, i, 2];
                    }
                    int num3 = k;
                    if (i == 1 || i == 3)
                    {
                        num3 = num - k - 1;
                    }
                    array[0, num3] = (byte)(b * (byte)bright >> 8);
                    array[1, num3] = (byte)(b2 * (byte)bright >> 8);
                    array[2, num3] = (byte)(b3 * (byte)bright >> 8);
                }
                int num4 = num2;
                for (int l = 0; l < fans; l++)
                {
                    int num5 = l * 44;
                    int num3 = l * 8;
                    if (pn == 1)
                    {
                        num3 = num3 + 8 - 1;
                    }
                    for (int k = num5; k < num5 + 8; k++)
                    {
                        frames[num4, 0, k] = 0;
                        frames[num4, 1, k] = 0;
                        frames[num4, 2, k] = 0;
                        if (ie == 2)
                        {
                            frames[num4, 0, k] = array[0, num3];
                            frames[num4, 1, k] = array[1, num3];
                            frames[num4, 2, k] = array[2, num3];
                            num3 = ((pn != 0) ? (num3 - 1) : (num3 + 1));
                        }
                    }
                    num3 = l * 10;
                    if (pn == 1)
                    {
                        num3 = num3 + 10 - 1;
                    }
                    for (int k = num5 + 8; k < num5 + 18; k++)
                    {
                        frames[num4, 0, k] = 0;
                        frames[num4, 1, k] = 0;
                        frames[num4, 2, k] = 0;
                        if (ie == 1)
                        {
                            frames[num4, 0, k] = array[0, num3];
                            frames[num4, 1, k] = array[1, num3];
                            frames[num4, 2, k] = array[2, num3];
                            num3 = ((pn != 0) ? (num3 - 1) : (num3 + 1));
                        }
                    }
                    num3 = l * 8;
                    if (pn == 1)
                    {
                        num3 = num3 + 8 - 1;
                    }
                    for (int k = num5 + 18; k < num5 + 26; k++)
                    {
                        frames[num4, 0, k] = 0;
                        frames[num4, 1, k] = 0;
                        frames[num4, 2, k] = 0;
                        if (ie == 0)
                        {
                            frames[num4, 0, k] = array[0, num3];
                            frames[num4, 1, k] = array[1, num3];
                            frames[num4, 2, k] = array[2, num3];
                            num3 = ((pn != 0) ? (num3 - 1) : (num3 + 1));
                        }
                    }
                    num3 = l * 10;
                    if (pn == 1)
                    {
                        num3 = num3 + 10 - 1;
                    }
                    for (int k = num5 + 26; k < num5 + 36; k++)
                    {
                        frames[num4, 0, k] = 0;
                        frames[num4, 1, k] = 0;
                        frames[num4, 2, k] = 0;
                        if (ie == 1)
                        {
                            frames[num4, 0, k] = array[0, num3];
                            frames[num4, 1, k] = array[1, num3];
                            frames[num4, 2, k] = array[2, num3];
                            num3 = ((pn != 0) ? (num3 - 1) : (num3 + 1));
                        }
                    }
                    num3 = l * 8;
                    if (pn == 1)
                    {
                        num3 = num3 + 8 - 1;
                    }
                    for (int k = num5 + 36; k < num5 + 44; k++)
                    {
                        frames[num4, 0, k] = 0;
                        frames[num4, 1, k] = 0;
                        frames[num4, 2, k] = 0;
                        if (ie == 0)
                        {
                            frames[num4, 0, k] = array[0, num3];
                            frames[num4, 1, k] = array[1, num3];
                            frames[num4, 2, k] = array[2, num3];
                            num3 = ((pn != 0) ? (num3 - 1) : (num3 + 1));
                        }
                    }
                }
                num2++;
            }
        }
        return num2;
    }
    internal int InfMeteorRainbow(int ie, int fans, int bright, int direction, int pn)
    {
        byte[,] array = new byte[3, 176];
        byte[,] array2 = new byte[3, 4]
        {
            { 8, 16, 24, 32 },
            { 10, 20, 30, 40 },
            { 8, 16, 24, 32 }
        };
        byte[,] array3 = new byte[4, 8]
        {
            { 255, 255, 0, 0, 0, 0, 0, 0 },
            { 255, 255, 255, 255, 0, 0, 0, 0 },
            { 255, 255, 255, 255, 255, 255, 0, 0 },
            { 255, 255, 255, 255, 255, 255, 255, 255 }
        };
        byte[,] array4 = new byte[3, 45];
        byte[,] array5 = new byte[3, 45]
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
        int num3 = 0;
        for (int i = 0; i < num + fans + fans - 1; i++)
        {
            int num4 = num3;
            for (int j = 0; j < 45; j++)
            {
                int num5 = 45 - j - 1;
                array4[0, num5] = array5[0, num4];
                array4[1, num5] = array5[1, num4];
                array4[2, num5] = array5[2, num4];
                num4++;
                if (num4 >= 45)
                {
                    num4 -= 45;
                }
            }
            num3++;
            if (num3 >= 45)
            {
                num3 -= 45;
            }
            num3++;
            if (num3 >= 45)
            {
                num3 -= 45;
            }
            int num6 = 0;
            for (int j = 0; j < num; j++)
            {
                int num7 = 0;
                int num8 = 0;
                int num9 = 0;
                if (j <= i && j + fans + fans > i)
                {
                    if (array3[fans - 1, num6] != 0)
                    {
                        num7 = array4[0, j];
                        num8 = array4[1, j];
                        num9 = array4[2, j];
                    }
                    num6++;
                }
                int num5 = j;
                if (direction != 0)
                {
                    num5 = num - j - 1;
                }
                array[0, num5] = (byte)(num7 * bright >> 8);
                array[1, num5] = (byte)(num8 * bright >> 8);
                array[2, num5] = (byte)(num9 * bright >> 8);
            }
            int num10 = num2;
            for (num4 = 0; num4 < fans; num4++)
            {
                int num11 = num4 * 44;
                int num5 = num4 * 8;
                if (pn == 0)
                {
                    num5 = num5 + 8 - 1;
                }
                for (int j = num11; j < num11 + 8; j++)
                {
                    frames[num10, 0, j] = 0;
                    frames[num10, 1, j] = 0;
                    frames[num10, 2, j] = 0;
                    if (ie == 2)
                    {
                        frames[num10, 0, j] = array[0, num5];
                        frames[num10, 1, j] = array[1, num5];
                        frames[num10, 2, j] = array[2, num5];
                        num5 = ((pn != 1) ? (num5 - 1) : (num5 + 1));
                    }
                }
                num5 = num4 * 10;
                if (pn == 0)
                {
                    num5 = num5 + 10 - 1;
                }
                for (int j = num11 + 8; j < num11 + 18; j++)
                {
                    frames[num10, 0, j] = 0;
                    frames[num10, 1, j] = 0;
                    frames[num10, 2, j] = 0;
                    if (ie == 1)
                    {
                        frames[num10, 0, j] = array[0, num5];
                        frames[num10, 1, j] = array[1, num5];
                        frames[num10, 2, j] = array[2, num5];
                        num5 = ((pn != 1) ? (num5 - 1) : (num5 + 1));
                    }
                }
                num5 = num4 * 8;
                if (pn == 0)
                {
                    num5 = num5 + 8 - 1;
                }
                for (int j = num11 + 18; j < num11 + 26; j++)
                {
                    frames[num10, 0, j] = 0;
                    frames[num10, 1, j] = 0;
                    frames[num10, 2, j] = 0;
                    if (ie == 0)
                    {
                        frames[num10, 0, j] = array[0, num5];
                        frames[num10, 1, j] = array[1, num5];
                        frames[num10, 2, j] = array[2, num5];
                        num5 = ((pn != 1) ? (num5 - 1) : (num5 + 1));
                    }
                }
                num5 = num4 * 10;
                if (pn == 0)
                {
                    num5 = num5 + 10 - 1;
                }
                for (int j = num11 + 26; j < num11 + 36; j++)
                {
                    frames[num10, 0, j] = 0;
                    frames[num10, 1, j] = 0;
                    frames[num10, 2, j] = 0;
                    if (ie == 1)
                    {
                        frames[num10, 0, j] = array[0, num5];
                        frames[num10, 1, j] = array[1, num5];
                        frames[num10, 2, j] = array[2, num5];
                        num5 = ((pn != 1) ? (num5 - 1) : (num5 + 1));
                    }
                }
                num5 = num4 * 8;
                if (pn == 0)
                {
                    num5 = num5 + 8 - 1;
                }
                for (int j = num11 + 36; j < num11 + 44; j++)
                {
                    frames[num10, 0, j] = 0;
                    frames[num10, 1, j] = 0;
                    frames[num10, 2, j] = 0;
                    if (ie == 0)
                    {
                        frames[num10, 0, j] = array[0, num5];
                        frames[num10, 1, j] = array[1, num5];
                        frames[num10, 2, j] = array[2, num5];
                        num5 = ((pn != 1) ? (num5 - 1) : (num5 + 1));
                    }
                }
            }
            num2++;
        }
        return num2;
    }
    internal int InfWarning(int ie, int fans, int bright, int direction, int pn)
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
        byte b = array2[ie, fans - 1];
        int num = 0;
        for (int i = 0; i < 4; i++)
        {
            for (int j = 0; j < 2; j++)
            {
                for (int k = 0; k < 8; k++)
                {
                    for (int l = 0; l < b; l++)
                    {
                        array[0, l] = 0;
                        array[1, l] = 0;
                        array[2, l] = 0;
                        if ((j == 0 && ie == 2) || (j == 1 && ie != 2))
                        {
                            array[0, l] = (byte)(palette[ie, i, 0] * bright >> 8);
                            array[1, l] = (byte)(palette[ie, i, 1] * bright >> 8);
                            array[2, l] = (byte)(palette[ie, i, 2] * bright >> 8);
                        }
                    }
                    int num2 = num;
                    for (int m = 0; m < fans; m++)
                    {
                        int num3 = m * 44;
                        int num4 = m * 8;
                        if (pn == 0)
                        {
                            num4 = num4 + 8 - 1;
                        }
                        for (int l = num3; l < num3 + 8; l++)
                        {
                            frames[num2, 0, l] = 0;
                            frames[num2, 1, l] = 0;
                            frames[num2, 2, l] = 0;
                            if (ie == 2)
                            {
                                frames[num2, 0, l] = array[0, num4];
                                frames[num2, 1, l] = array[1, num4];
                                frames[num2, 2, l] = array[2, num4];
                                num4 = ((pn != 1) ? (num4 - 1) : (num4 + 1));
                            }
                        }
                        num4 = m * 10;
                        if (pn == 0)
                        {
                            num4 = num4 + 10 - 1;
                        }
                        for (int l = num3 + 8; l < num3 + 18; l++)
                        {
                            frames[num2, 0, l] = 0;
                            frames[num2, 1, l] = 0;
                            frames[num2, 2, l] = 0;
                            if (ie == 1)
                            {
                                frames[num2, 0, l] = array[0, num4];
                                frames[num2, 1, l] = array[1, num4];
                                frames[num2, 2, l] = array[2, num4];
                                num4 = ((pn != 1) ? (num4 - 1) : (num4 + 1));
                            }
                        }
                        num4 = m * 8;
                        if (pn == 0)
                        {
                            num4 = num4 + 8 - 1;
                        }
                        for (int l = num3 + 18; l < num3 + 26; l++)
                        {
                            frames[num2, 0, l] = 0;
                            frames[num2, 1, l] = 0;
                            frames[num2, 2, l] = 0;
                            if (ie == 0)
                            {
                                frames[num2, 0, l] = array[0, num4];
                                frames[num2, 1, l] = array[1, num4];
                                frames[num2, 2, l] = array[2, num4];
                                num4 = ((pn != 1) ? (num4 - 1) : (num4 + 1));
                            }
                        }
                        num4 = m * 10;
                        if (pn == 0)
                        {
                            num4 = num4 + 10 - 1;
                        }
                        for (int l = num3 + 26; l < num3 + 36; l++)
                        {
                            frames[num2, 0, l] = 0;
                            frames[num2, 1, l] = 0;
                            frames[num2, 2, l] = 0;
                            if (ie == 1)
                            {
                                frames[num2, 0, l] = array[0, num4];
                                frames[num2, 1, l] = array[1, num4];
                                frames[num2, 2, l] = array[2, num4];
                                num4 = ((pn != 1) ? (num4 - 1) : (num4 + 1));
                            }
                        }
                        num4 = m * 8;
                        if (pn == 0)
                        {
                            num4 = num4 + 8 - 1;
                        }
                        for (int l = num3 + 36; l < num3 + 44; l++)
                        {
                            frames[num2, 0, l] = 0;
                            frames[num2, 1, l] = 0;
                            frames[num2, 2, l] = 0;
                            if (ie == 0)
                            {
                                frames[num2, 0, l] = array[0, num4];
                                frames[num2, 1, l] = array[1, num4];
                                frames[num2, 2, l] = array[2, num4];
                                num4 = ((pn != 1) ? (num4 - 1) : (num4 + 1));
                            }
                        }
                    }
                    num++;
                }
            }
        }
        return num;
    }
    internal int InfVoice(int ie, int fans, int bright, int direction, int pn)
    {
        byte[,] array = new byte[3, 176];
        byte[] array2 = new byte[32]
        {
            3, 4, 2, 5, 1, 6, 8, 7, 11, 12,
            10, 13, 9, 14, 16, 15, 19, 20, 18, 21,
            17, 22, 24, 23, 27, 28, 26, 29, 25, 30,
            32, 31
        };
        byte[] array3 = new byte[32]
        {
            7, 8, 6, 1, 5, 2, 4, 3, 15, 16,
            14, 9, 13, 10, 12, 11, 23, 24, 22, 17,
            21, 18, 20, 19, 31, 32, 30, 25, 29, 26,
            28, 27
        };
        byte[,] array4 = new byte[3, 4]
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
        byte b = array4[ie, fans - 1];
        int num = 0;
        for (int i = 0; i < 4; i++)
        {
            for (int j = 0; j < 3; j++)
            {
                for (int k = 0; k < 2; k++)
                {
                    int num2 = j switch
                    {
                        0 => b, 
                        1 => b * 2 / 3, 
                        _ => b / 3, 
                    };
                    for (int l = 0; l < num2; l++)
                    {
                        for (int m = 0; m < b; m++)
                        {
                            int num3 = m;
                            if (direction == 1)
                            {
                                num3 = b - m - 1;
                            }
                            if (ie == 2 && pn == 0)
                            {
                                num3 = array2[num3] - 1;
                            }
                            if (ie == 2 && pn == 1)
                            {
                                num3 = array3[num3] - 1;
                            }
                            array[0, num3] = (byte)(palette[ie, i, 0] * bright >> 8);
                            array[1, num3] = (byte)(palette[ie, i, 1] * bright >> 8);
                            array[2, num3] = (byte)(palette[ie, i, 2] * bright >> 8);
                            if (m > l && k == 0)
                            {
                                array[0, num3] /= 16;
                                array[1, num3] /= 16;
                                array[2, num3] /= 16;
                            }
                            if (m > num2 - l && k == 1)
                            {
                                array[0, num3] /= 16;
                                array[1, num3] /= 16;
                                array[2, num3] /= 16;
                            }
                        }
                        int num4 = num;
                        for (int n = 0; n < fans; n++)
                        {
                            int num5 = n * 44;
                            int num3 = n * 8;
                            for (int m = num5; m < num5 + 8; m++)
                            {
                                frames[num4, 0, m] = 0;
                                frames[num4, 1, m] = 0;
                                frames[num4, 2, m] = 0;
                                if (ie == 2)
                                {
                                    frames[num4, 0, m] = array[0, num3];
                                    frames[num4, 1, m] = array[1, num3];
                                    frames[num4, 2, m] = array[2, num3];
                                    num3++;
                                }
                            }
                            num3 = n * 10;
                            if (pn == 0)
                            {
                                num3 = num3 + 10 - 1;
                            }
                            for (int m = num5 + 8; m < num5 + 18; m++)
                            {
                                frames[num4, 0, m] = 0;
                                frames[num4, 1, m] = 0;
                                frames[num4, 2, m] = 0;
                                if (ie == 1)
                                {
                                    frames[num4, 0, m] = array[0, num3];
                                    frames[num4, 1, m] = array[1, num3];
                                    frames[num4, 2, m] = array[2, num3];
                                    num3 = ((pn != 1) ? (num3 - 1) : (num3 + 1));
                                }
                            }
                            num3 = n * 8;
                            if (pn == 0)
                            {
                                num3 = num3 + 8 - 1;
                            }
                            for (int m = num5 + 18; m < num5 + 26; m++)
                            {
                                frames[num4, 0, m] = 0;
                                frames[num4, 1, m] = 0;
                                frames[num4, 2, m] = 0;
                                if (ie == 0)
                                {
                                    frames[num4, 0, m] = array[0, num3];
                                    frames[num4, 1, m] = array[1, num3];
                                    frames[num4, 2, m] = array[2, num3];
                                    num3 = ((pn != 1) ? (num3 - 1) : (num3 + 1));
                                }
                            }
                            num3 = n * 10;
                            if (pn == 0)
                            {
                                num3 = num3 + 10 - 1;
                            }
                            for (int m = num5 + 26; m < num5 + 36; m++)
                            {
                                frames[num4, 0, m] = 0;
                                frames[num4, 1, m] = 0;
                                frames[num4, 2, m] = 0;
                                if (ie == 1)
                                {
                                    frames[num4, 0, m] = array[0, num3];
                                    frames[num4, 1, m] = array[1, num3];
                                    frames[num4, 2, m] = array[2, num3];
                                    num3 = ((pn != 1) ? (num3 - 1) : (num3 + 1));
                                }
                            }
                            num3 = n * 8;
                            if (pn == 0)
                            {
                                num3 = num3 + 8 - 1;
                            }
                            for (int m = num5 + 36; m < num5 + 44; m++)
                            {
                                frames[num4, 0, m] = 0;
                                frames[num4, 1, m] = 0;
                                frames[num4, 2, m] = 0;
                                if (ie == 0)
                                {
                                    frames[num4, 0, m] = array[0, num3];
                                    frames[num4, 1, m] = array[1, num3];
                                    frames[num4, 2, m] = array[2, num3];
                                    num3 = ((pn != 1) ? (num3 - 1) : (num3 + 1));
                                }
                            }
                        }
                        num++;
                    }
                }
            }
        }
        int num6 = num / 2;
        byte[,,] array5 = new byte[num6, 3, ledCount];
        for (int num7 = 0; num7 < num6; num7++)
        {
            for (int num8 = 0; num8 < 3; num8++)
            {
                for (int num9 = 0; num9 < ledCount; num9++)
                {
                    array5[num7, num8, num9] = frames[num7 * 2, num8, num9];
                }
            }
        }
        frames = array5;
        return num6;
    }
    internal int InfMixing(int ie, int fans, int bright, int direction, int pn)
    {
        byte[,] array = new byte[3, 176];
        byte[,] array2 = new byte[3, 3];
        byte[,] array3 = new byte[3, 4]
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
        byte b = (byte)(array3[ie, fans - 1] / 2);
        for (int i = 0; i < 3; i++)
        {
            for (int j = 0; j < 3; j++)
            {
                if (palette[i, 0, j] + palette[i, 1, j] >= 255)
                {
                    array2[i, j] = byte.MaxValue;
                }
                else
                {
                    array2[i, j] = (byte)(palette[i, 0, j] + palette[i, 1, j]);
                }
            }
        }
        for (int i = 0; i < 3; i++)
        {
            if (array2[i, 0] + array2[i, 1] + array2[i, 2] > PowerCap)
            {
                var (b2, b3, b4) = GetRgb(array2[i, 0], array2[i, 1], array2[i, 2]);
                array2[i, 0] = b2;
                array2[i, 1] = b3;
                array2[i, 2] = b4;
            }
        }
        int num = 0;
        for (int k = 0; k < 3; k++)
        {
            for (int l = 0; l < b + fans + fans - 1; l++)
            {
                for (int j = 0; j < b; j++)
                {
                    byte b5 = 0;
                    byte b6 = 0;
                    byte b7 = 0;
                    if ((k == 1 && j < l) || (k == 2 && j + fans + fans > l))
                    {
                        b5 = array2[ie, 0];
                        b6 = array2[ie, 1];
                        b7 = array2[ie, 2];
                    }
                    else if (j < l && j + fans + fans > l)
                    {
                        b5 = palette[ie, 0, 0];
                        b6 = palette[ie, 0, 1];
                        b7 = palette[ie, 0, 2];
                    }
                    int num2 = j;
                    if (k != 0)
                    {
                        num2 = b - j - 1;
                    }
                    array[0, num2] = (byte)(b5 * bright >> 8);
                    array[1, num2] = (byte)(b6 * bright >> 8);
                    array[2, num2] = (byte)(b7 * bright >> 8);
                }
                for (int j = 0; j < b; j++)
                {
                    byte b5 = 0;
                    byte b6 = 0;
                    byte b7 = 0;
                    if ((k == 1 && j < l) || (k == 2 && j + fans + fans > l))
                    {
                        b5 = array2[ie, 0];
                        b6 = array2[ie, 1];
                        b7 = array2[ie, 2];
                    }
                    else if (j < l && j + fans + fans > l)
                    {
                        b5 = palette[ie, 1, 0];
                        b6 = palette[ie, 1, 1];
                        b7 = palette[ie, 1, 2];
                    }
                    int num2 = b - j - 1;
                    if (k != 0)
                    {
                        num2 = j;
                    }
                    array[0, num2 + b] = (byte)(b5 * bright >> 8);
                    array[1, num2 + b] = (byte)(b6 * bright >> 8);
                    array[2, num2 + b] = (byte)(b7 * bright >> 8);
                }
                if (k != 0)
                {
                    l++;
                }
                int num3 = num;
                for (int i = 0; i < fans; i++)
                {
                    int num4 = i * 44;
                    int num2 = i * 8;
                    for (int j = num4; j < num4 + 8; j++)
                    {
                        frames[num3, 0, j] = 0;
                        frames[num3, 1, j] = 0;
                        frames[num3, 2, j] = 0;
                        if (ie == 2)
                        {
                            frames[num3, 0, j] = array[0, num2];
                            frames[num3, 1, j] = array[1, num2];
                            frames[num3, 2, j] = array[2, num2];
                            num2++;
                        }
                    }
                    num2 = i * 10;
                    if (pn == 0)
                    {
                        num2 = num2 + 10 - 1;
                    }
                    for (int j = num4 + 8; j < num4 + 18; j++)
                    {
                        frames[num3, 0, j] = 0;
                        frames[num3, 1, j] = 0;
                        frames[num3, 2, j] = 0;
                        if (ie == 1)
                        {
                            frames[num3, 0, j] = array[0, num2];
                            frames[num3, 1, j] = array[1, num2];
                            frames[num3, 2, j] = array[2, num2];
                            num2 = ((pn != 1) ? (num2 - 1) : (num2 + 1));
                        }
                    }
                    num2 = i * 8;
                    if (pn == 0)
                    {
                        num2 = num2 + 8 - 1;
                    }
                    for (int j = num4 + 18; j < num4 + 26; j++)
                    {
                        frames[num3, 0, j] = 0;
                        frames[num3, 1, j] = 0;
                        frames[num3, 2, j] = 0;
                        if (ie == 0)
                        {
                            frames[num3, 0, j] = array[0, num2];
                            frames[num3, 1, j] = array[1, num2];
                            frames[num3, 2, j] = array[2, num2];
                            num2 = ((pn != 1) ? (num2 - 1) : (num2 + 1));
                        }
                    }
                    num2 = i * 10;
                    if (pn == 0)
                    {
                        num2 = num2 + 10 - 1;
                    }
                    for (int j = num4 + 26; j < num4 + 36; j++)
                    {
                        frames[num3, 0, j] = 0;
                        frames[num3, 1, j] = 0;
                        frames[num3, 2, j] = 0;
                        if (ie == 1)
                        {
                            frames[num3, 0, j] = array[0, num2];
                            frames[num3, 1, j] = array[1, num2];
                            frames[num3, 2, j] = array[2, num2];
                            num2 = ((pn != 1) ? (num2 - 1) : (num2 + 1));
                        }
                    }
                    num2 = i * 8;
                    if (pn == 0)
                    {
                        num2 = num2 + 8 - 1;
                    }
                    for (int j = num4 + 36; j < num4 + 44; j++)
                    {
                        frames[num3, 0, j] = 0;
                        frames[num3, 1, j] = 0;
                        frames[num3, 2, j] = 0;
                        if (ie == 0)
                        {
                            frames[num3, 0, j] = array[0, num2];
                            frames[num3, 1, j] = array[1, num2];
                            frames[num3, 2, j] = array[2, num2];
                            num2 = ((pn != 1) ? (num2 - 1) : (num2 + 1));
                        }
                    }
                }
                num++;
            }
        }
        return num;
    }
    internal int InfTide(int ie, int fans, int bright, int direction, int pn)
    {
        byte[,] array = new byte[3, 176];
        byte[] array2 = new byte[32]
        {
            3, 4, 2, 5, 1, 6, 8, 7, 11, 12,
            10, 13, 9, 14, 16, 15, 19, 20, 18, 21,
            17, 22, 24, 23, 27, 28, 26, 29, 25, 30,
            32, 31
        };
        byte[] array3 = new byte[32]
        {
            7, 8, 6, 1, 5, 2, 4, 3, 15, 16,
            14, 9, 13, 10, 12, 11, 23, 24, 22, 17,
            21, 18, 20, 19, 31, 32, 30, 25, 29, 26,
            28, 27
        };
        byte[,] array4 = new byte[3, 4]
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
        byte b = (byte)(array4[ie, fans - 1] / 2);
        int num = 0;
        for (int i = 0; i < 4; i++)
        {
            for (int j = 0; j < b + fans + fans - 1; j++)
            {
                for (int k = 0; k < b; k++)
                {
                    int num2 = ((i != 0) ? (i - 1) : 3);
                    if (k <= j)
                    {
                        num2 = i;
                    }
                    byte b2 = palette[ie, num2, 0];
                    byte b3 = palette[ie, num2, 1];
                    byte b4 = palette[ie, num2, 2];
                    num2 = k;
                    if (ie == 2 && pn == 0)
                    {
                        num2 = array2[num2] - 1;
                    }
                    if (ie == 2 && pn == 1)
                    {
                        num2 = array3[num2] - 1;
                    }
                    array[0, num2] = (byte)(b2 * bright >> 8);
                    array[1, num2] = (byte)(b3 * bright >> 8);
                    array[2, num2] = (byte)(b4 * bright >> 8);
                }
                for (int k = 0; k < b; k++)
                {
                    int num2 = ((i != 0) ? (i - 1) : 3);
                    if (k <= j)
                    {
                        num2 = i;
                    }
                    byte b2 = palette[ie, num2, 0];
                    byte b3 = palette[ie, num2, 1];
                    byte b4 = palette[ie, num2, 2];
                    num2 = (byte)(b - k - 1 + b);
                    if (ie == 2 && pn == 0)
                    {
                        num2 = array2[num2] - 1;
                    }
                    if (ie == 2 && pn == 1)
                    {
                        num2 = array3[num2] - 1;
                    }
                    array[0, num2] = (byte)(b2 * bright >> 8);
                    array[1, num2] = (byte)(b3 * bright >> 8);
                    array[2, num2] = (byte)(b4 * bright >> 8);
                }
                int num3 = num;
                for (int l = 0; l < fans; l++)
                {
                    int num4 = l * 44;
                    int num2 = l * 8;
                    for (int k = num4; k < num4 + 8; k++)
                    {
                        frames[num3, 0, k] = 0;
                        frames[num3, 1, k] = 0;
                        frames[num3, 2, k] = 0;
                        if (ie == 2)
                        {
                            frames[num3, 0, k] = array[0, num2];
                            frames[num3, 1, k] = array[1, num2];
                            frames[num3, 2, k] = array[2, num2];
                            num2++;
                        }
                    }
                    num2 = l * 10;
                    if (pn == 0)
                    {
                        num2 = num2 + 10 - 1;
                    }
                    for (int k = num4 + 8; k < num4 + 18; k++)
                    {
                        frames[num3, 0, k] = 0;
                        frames[num3, 1, k] = 0;
                        frames[num3, 2, k] = 0;
                        if (ie == 1)
                        {
                            frames[num3, 0, k] = array[0, num2];
                            frames[num3, 1, k] = array[1, num2];
                            frames[num3, 2, k] = array[2, num2];
                            num2 = ((pn != 1) ? (num2 - 1) : (num2 + 1));
                        }
                    }
                    num2 = l * 8;
                    if (pn == 0)
                    {
                        num2 = num2 + 8 - 1;
                    }
                    for (int k = num4 + 18; k < num4 + 26; k++)
                    {
                        frames[num3, 0, k] = 0;
                        frames[num3, 1, k] = 0;
                        frames[num3, 2, k] = 0;
                        if (ie == 0)
                        {
                            frames[num3, 0, k] = array[0, num2];
                            frames[num3, 1, k] = array[1, num2];
                            frames[num3, 2, k] = array[2, num2];
                            num2 = ((pn != 1) ? (num2 - 1) : (num2 + 1));
                        }
                    }
                    num2 = l * 10;
                    if (pn == 0)
                    {
                        num2 = num2 + 10 - 1;
                    }
                    for (int k = num4 + 26; k < num4 + 36; k++)
                    {
                        frames[num3, 0, k] = 0;
                        frames[num3, 1, k] = 0;
                        frames[num3, 2, k] = 0;
                        if (ie == 1)
                        {
                            frames[num3, 0, k] = array[0, num2];
                            frames[num3, 1, k] = array[1, num2];
                            frames[num3, 2, k] = array[2, num2];
                            num2 = ((pn != 1) ? (num2 - 1) : (num2 + 1));
                        }
                    }
                    num2 = l * 8;
                    if (pn == 0)
                    {
                        num2 = num2 + 8 - 1;
                    }
                    for (int k = num4 + 36; k < num4 + 44; k++)
                    {
                        frames[num3, 0, k] = 0;
                        frames[num3, 1, k] = 0;
                        frames[num3, 2, k] = 0;
                        if (ie == 0)
                        {
                            frames[num3, 0, k] = array[0, num2];
                            frames[num3, 1, k] = array[1, num2];
                            frames[num3, 2, k] = array[2, num2];
                            num2 = ((pn != 1) ? (num2 - 1) : (num2 + 1));
                        }
                    }
                }
                num++;
            }
        }
        return num;
    }
    internal int InfScan(int ie, int fans, int bright, int direction, int pn)
    {
        byte[,] array = new byte[3, 176];
        byte[,] array2 = new byte[3, 4]
        {
            { 8, 16, 24, 32 },
            { 10, 20, 30, 40 },
            { 8, 16, 24, 32 }
        };
        byte[] array3 = new byte[32]
        {
            3, 4, 2, 5, 1, 6, 8, 7, 11, 12,
            10, 13, 9, 14, 16, 15, 19, 20, 18, 21,
            17, 22, 24, 23, 27, 28, 26, 29, 25, 30,
            32, 31
        };
        byte[] array4 = new byte[32]
        {
            7, 8, 6, 1, 5, 2, 4, 3, 15, 16,
            14, 9, 13, 10, 12, 11, 23, 24, 22, 17,
            21, 18, 20, 19, 31, 32, 30, 25, 29, 26,
            28, 27
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
        byte b = array2[ie, fans - 1];
        int num = 0;
        for (int i = 0; i < 2; i++)
        {
            for (int j = 0; j < b + fans + fans; j++)
            {
                for (int k = 0; k < b; k++)
                {
                    byte b2 = 0;
                    byte b3 = 0;
                    byte b4 = 0;
                    if (k <= j && k + fans > j)
                    {
                        b2 = palette[ie, 0, 0];
                        b3 = palette[ie, 0, 1];
                        b4 = palette[ie, 0, 2];
                    }
                    int num2 = k;
                    if (i == 1)
                    {
                        num2 = b - k - 1;
                    }
                    if (ie == 2 && pn == 0)
                    {
                        num2 = array3[k] - 1;
                        if (i == 1)
                        {
                            num2 = array3[b - k - 1] - 1;
                        }
                    }
                    if (ie == 2 && pn == 1)
                    {
                        num2 = array4[k] - 1;
                        if (i == 1)
                        {
                            num2 = array4[b - k - 1] - 1;
                        }
                    }
                    array[0, num2] = (byte)(b2 * bright >> 8);
                    array[1, num2] = (byte)(b3 * bright >> 8);
                    array[2, num2] = (byte)(b4 * bright >> 8);
                }
                int num3 = num;
                for (int l = 0; l < fans; l++)
                {
                    int num4 = l * 44;
                    int num2 = l * 8;
                    for (int k = num4; k < num4 + 8; k++)
                    {
                        frames[num3, 0, k] = 0;
                        frames[num3, 1, k] = 0;
                        frames[num3, 2, k] = 0;
                        if (ie == 2)
                        {
                            frames[num3, 0, k] = array[0, num2];
                            frames[num3, 1, k] = array[1, num2];
                            frames[num3, 2, k] = array[2, num2];
                            num2++;
                        }
                    }
                    num2 = l * 10;
                    if (pn == 0)
                    {
                        num2 = num2 + 10 - 1;
                    }
                    for (int k = num4 + 8; k < num4 + 18; k++)
                    {
                        frames[num3, 0, k] = 0;
                        frames[num3, 1, k] = 0;
                        frames[num3, 2, k] = 0;
                        if (ie == 1)
                        {
                            frames[num3, 0, k] = array[0, num2];
                            frames[num3, 1, k] = array[1, num2];
                            frames[num3, 2, k] = array[2, num2];
                            num2 = ((pn != 1) ? (num2 - 1) : (num2 + 1));
                        }
                    }
                    num2 = l * 8;
                    if (pn == 0)
                    {
                        num2 = num2 + 8 - 1;
                    }
                    for (int k = num4 + 18; k < num4 + 26; k++)
                    {
                        frames[num3, 0, k] = 0;
                        frames[num3, 1, k] = 0;
                        frames[num3, 2, k] = 0;
                        if (ie == 0)
                        {
                            frames[num3, 0, k] = array[0, num2];
                            frames[num3, 1, k] = array[1, num2];
                            frames[num3, 2, k] = array[2, num2];
                            num2 = ((pn != 1) ? (num2 - 1) : (num2 + 1));
                        }
                    }
                    num2 = l * 10;
                    if (pn == 0)
                    {
                        num2 = num2 + 10 - 1;
                    }
                    for (int k = num4 + 26; k < num4 + 36; k++)
                    {
                        frames[num3, 0, k] = 0;
                        frames[num3, 1, k] = 0;
                        frames[num3, 2, k] = 0;
                        if (ie == 1)
                        {
                            frames[num3, 0, k] = array[0, num2];
                            frames[num3, 1, k] = array[1, num2];
                            frames[num3, 2, k] = array[2, num2];
                            num2 = ((pn != 1) ? (num2 - 1) : (num2 + 1));
                        }
                    }
                    num2 = l * 8;
                    if (pn == 0)
                    {
                        num2 = num2 + 8 - 1;
                    }
                    for (int k = num4 + 36; k < num4 + 44; k++)
                    {
                        frames[num3, 0, k] = 0;
                        frames[num3, 1, k] = 0;
                        frames[num3, 2, k] = 0;
                        if (ie == 0)
                        {
                            frames[num3, 0, k] = array[0, num2];
                            frames[num3, 1, k] = array[1, num2];
                            frames[num3, 2, k] = array[2, num2];
                            num2 = ((pn != 1) ? (num2 - 1) : (num2 + 1));
                        }
                    }
                }
                num++;
            }
        }
        return num;
    }
}
