using System;

namespace Nexus.Service.Peripherals.LianLiFlex;

internal sealed partial class FlexSlInfModes
{
    internal int InfWing(int ie, int fans, int bright, int direction, int pn)
    {
        byte[,,] array = new byte[5, 3, 40];
        byte[,] array2 = new byte[12, 24]
        {
            {
                0, 0, 0, 0, 0, 0, 0, 0, 0, 0,
                0, 0, 3, 0, 0, 0, 0, 0, 0, 0,
                0, 0, 0, 0
            },
            {
                0, 0, 0, 0, 0, 0, 0, 0, 0, 0,
                0, 0, 3, 0, 0, 0, 0, 0, 0, 0,
                0, 0, 0, 0
            },
            {
                0, 0, 0, 0, 0, 0, 0, 0, 0, 0,
                0, 0, 3, 0, 0, 0, 0, 0, 0, 0,
                0, 0, 0, 0
            },
            {
                0, 0, 0, 0, 0, 0, 0, 0, 0, 0,
                0, 1, 3, 2, 0, 0, 0, 0, 0, 0,
                0, 0, 0, 0
            },
            {
                0, 0, 0, 0, 0, 0, 0, 0, 0, 0,
                0, 1, 3, 2, 0, 0, 0, 0, 0, 0,
                0, 0, 0, 0
            },
            {
                0, 0, 0, 0, 0, 0, 0, 0, 0, 0,
                0, 1, 3, 2, 0, 0, 0, 0, 0, 0,
                0, 0, 0, 0
            },
            {
                0, 0, 0, 0, 0, 0, 0, 1, 0, 0,
                0, 1, 3, 2, 0, 0, 0, 2, 0, 0,
                0, 0, 0, 0
            },
            {
                0, 0, 0, 0, 0, 0, 0, 1, 0, 0,
                0, 1, 3, 2, 0, 0, 0, 2, 0, 0,
                0, 0, 0, 0
            },
            {
                1, 0, 0, 0, 0, 0, 0, 1, 0, 0,
                0, 1, 3, 2, 0, 0, 2, 2, 0, 0,
                0, 0, 0, 0
            },
            {
                1, 0, 0, 0, 0, 0, 0, 1, 0, 0,
                0, 1, 3, 2, 0, 0, 2, 2, 0, 0,
                0, 0, 0, 0
            },
            {
                1, 1, 0, 0, 0, 0, 0, 1, 0, 0,
                0, 1, 3, 2, 0, 0, 2, 2, 0, 0,
                0, 0, 0, 2
            },
            {
                1, 1, 0, 0, 0, 0, 0, 1, 0, 0,
                0, 1, 3, 2, 0, 0, 2, 2, 0, 0,
                0, 0, 0, 2
            }
        };
        byte[,] array3 = new byte[12, 24]
        {
            {
                0, 0, 0, 0, 0, 0, 0, 0, 3, 0,
                0, 0, 0, 0, 0, 0, 0, 0, 0, 0,
                0, 0, 0, 0
            },
            {
                0, 0, 0, 0, 0, 0, 0, 0, 3, 0,
                0, 0, 0, 0, 0, 0, 0, 0, 0, 0,
                0, 0, 0, 0
            },
            {
                0, 0, 0, 0, 0, 0, 0, 0, 3, 0,
                0, 0, 0, 0, 0, 0, 0, 0, 0, 0,
                0, 0, 0, 0
            },
            {
                0, 0, 0, 0, 0, 0, 0, 0, 3, 2,
                0, 0, 0, 0, 0, 1, 0, 0, 0, 0,
                0, 0, 0, 0
            },
            {
                0, 0, 0, 0, 0, 0, 0, 0, 3, 2,
                0, 0, 0, 0, 0, 1, 0, 0, 0, 0,
                0, 0, 0, 0
            },
            {
                0, 0, 0, 0, 0, 0, 0, 0, 3, 2,
                0, 0, 0, 0, 0, 1, 0, 0, 0, 0,
                0, 0, 0, 0
            },
            {
                0, 0, 0, 1, 0, 0, 0, 0, 3, 2,
                0, 0, 0, 0, 0, 1, 0, 0, 0, 0,
                0, 2, 0, 0
            },
            {
                0, 0, 0, 1, 0, 0, 0, 0, 3, 2,
                0, 0, 0, 0, 0, 1, 0, 0, 0, 0,
                0, 2, 0, 0
            },
            {
                0, 0, 0, 1, 1, 0, 0, 0, 3, 2,
                0, 0, 0, 0, 0, 1, 0, 0, 0, 0,
                2, 2, 0, 0
            },
            {
                0, 0, 0, 1, 1, 0, 0, 0, 3, 2,
                0, 0, 0, 0, 0, 1, 0, 0, 0, 0,
                2, 2, 0, 0
            },
            {
                0, 0, 0, 1, 1, 1, 0, 0, 3, 2,
                0, 0, 0, 0, 0, 1, 0, 0, 0, 2,
                2, 2, 0, 0
            },
            {
                0, 0, 0, 1, 1, 1, 0, 0, 3, 2,
                0, 0, 0, 0, 0, 1, 0, 0, 0, 2,
                2, 2, 0, 0
            }
        };
        byte[,] array4 = new byte[16, 32]
        {
            {
                0, 0, 0, 0, 0, 0, 0, 0, 0, 0,
                0, 0, 0, 1, 0, 0, 0, 0, 0, 2,
                0, 0, 0, 0, 0, 0, 0, 0, 0, 0,
                0, 0
            },
            {
                0, 0, 0, 0, 0, 0, 0, 0, 0, 0,
                0, 0, 0, 1, 0, 0, 0, 0, 0, 2,
                0, 0, 0, 0, 0, 0, 0, 0, 0, 0,
                0, 0
            },
            {
                0, 0, 0, 0, 0, 0, 0, 0, 0, 0,
                0, 0, 0, 1, 0, 0, 0, 0, 0, 2,
                0, 0, 0, 0, 0, 0, 0, 0, 0, 0,
                0, 0
            },
            {
                0, 0, 0, 0, 0, 0, 0, 0, 0, 0,
                0, 0, 1, 1, 0, 0, 0, 0, 0, 2,
                2, 0, 0, 0, 0, 0, 0, 0, 0, 0,
                0, 0
            },
            {
                0, 0, 0, 0, 0, 0, 0, 0, 0, 0,
                0, 0, 1, 1, 0, 0, 0, 0, 0, 2,
                2, 0, 0, 0, 0, 0, 0, 0, 0, 0,
                0, 0
            },
            {
                0, 0, 0, 0, 0, 0, 0, 0, 0, 0,
                0, 0, 1, 1, 0, 0, 0, 0, 0, 2,
                2, 0, 0, 0, 0, 0, 0, 0, 0, 0,
                0, 0
            },
            {
                0, 0, 0, 0, 0, 0, 0, 0, 0, 0,
                0, 1, 1, 1, 0, 0, 0, 0, 0, 2,
                2, 2, 0, 0, 0, 0, 0, 0, 0, 0,
                0, 0
            },
            {
                0, 0, 0, 0, 0, 0, 0, 0, 0, 0,
                0, 1, 1, 1, 0, 0, 0, 0, 0, 2,
                2, 2, 0, 0, 0, 0, 0, 0, 0, 0,
                0, 0
            },
            {
                0, 0, 0, 0, 0, 0, 0, 1, 0, 0,
                0, 1, 1, 1, 0, 0, 0, 0, 0, 2,
                2, 2, 0, 0, 0, 2, 0, 0, 0, 0,
                0, 0
            },
            {
                0, 0, 0, 0, 0, 0, 0, 1, 0, 0,
                0, 1, 1, 1, 0, 0, 0, 0, 0, 2,
                2, 2, 0, 0, 0, 2, 0, 0, 0, 0,
                0, 0
            },
            {
                0, 0, 0, 0, 0, 0, 0, 1, 0, 0,
                0, 1, 1, 1, 0, 0, 0, 0, 0, 2,
                2, 2, 0, 0, 0, 2, 0, 0, 0, 0,
                0, 0
            },
            {
                1, 0, 0, 0, 0, 0, 0, 1, 0, 0,
                0, 1, 1, 1, 0, 0, 0, 0, 0, 2,
                2, 2, 0, 0, 2, 2, 0, 0, 0, 0,
                0, 0
            },
            {
                1, 0, 0, 0, 0, 0, 0, 1, 0, 0,
                0, 1, 1, 1, 0, 0, 0, 0, 0, 2,
                2, 2, 0, 0, 2, 2, 0, 0, 0, 0,
                0, 0
            },
            {
                1, 0, 0, 0, 0, 0, 0, 1, 0, 0,
                0, 1, 1, 1, 0, 0, 0, 0, 0, 2,
                2, 2, 0, 0, 2, 2, 0, 0, 0, 0,
                0, 0
            },
            {
                1, 1, 0, 0, 0, 0, 0, 1, 0, 0,
                0, 1, 1, 1, 0, 0, 0, 0, 0, 2,
                2, 2, 0, 0, 2, 2, 0, 0, 0, 0,
                0, 2
            },
            {
                1, 1, 0, 0, 0, 0, 0, 1, 0, 0,
                0, 1, 1, 1, 0, 0, 0, 0, 0, 2,
                2, 2, 0, 0, 2, 2, 0, 0, 0, 0,
                0, 2
            }
        };
        byte[,] array5 = new byte[16, 32]
        {
            {
                0, 0, 0, 0, 0, 0, 0, 0, 0, 1,
                0, 0, 0, 0, 0, 0, 0, 0, 0, 0,
                0, 0, 0, 2, 0, 0, 0, 0, 0, 0,
                0, 0
            },
            {
                0, 0, 0, 0, 0, 0, 0, 0, 0, 1,
                0, 0, 0, 0, 0, 0, 0, 0, 0, 0,
                0, 0, 0, 2, 0, 0, 0, 0, 0, 0,
                0, 0
            },
            {
                0, 0, 0, 0, 0, 0, 0, 0, 0, 1,
                0, 0, 0, 0, 0, 0, 0, 0, 0, 0,
                0, 0, 0, 2, 0, 0, 0, 0, 0, 0,
                0, 0
            },
            {
                0, 0, 0, 0, 0, 0, 0, 0, 1, 1,
                0, 0, 0, 0, 0, 0, 2, 0, 0, 0,
                0, 0, 0, 2, 0, 0, 0, 0, 0, 0,
                0, 0
            },
            {
                0, 0, 0, 0, 0, 0, 0, 0, 1, 1,
                0, 0, 0, 0, 0, 0, 2, 0, 0, 0,
                0, 0, 0, 2, 0, 0, 0, 0, 0, 0,
                0, 0
            },
            {
                0, 0, 0, 0, 0, 0, 0, 0, 1, 1,
                0, 0, 0, 0, 0, 0, 2, 0, 0, 0,
                0, 0, 0, 2, 0, 0, 0, 0, 0, 0,
                0, 0
            },
            {
                0, 0, 0, 0, 0, 0, 0, 0, 1, 1,
                0, 0, 0, 0, 0, 1, 2, 2, 0, 0,
                0, 0, 0, 2, 0, 0, 0, 0, 0, 0,
                0, 0
            },
            {
                0, 0, 0, 0, 0, 0, 0, 0, 1, 1,
                0, 0, 0, 0, 0, 1, 2, 2, 0, 0,
                0, 0, 0, 2, 0, 0, 0, 0, 0, 0,
                0, 0
            },
            {
                0, 0, 0, 1, 0, 0, 0, 0, 1, 1,
                0, 0, 0, 0, 0, 1, 2, 2, 0, 0,
                0, 0, 0, 2, 0, 0, 0, 0, 0, 2,
                0, 0
            },
            {
                0, 0, 0, 1, 0, 0, 0, 0, 1, 1,
                0, 0, 0, 0, 0, 1, 2, 2, 0, 0,
                0, 0, 0, 2, 0, 0, 0, 0, 0, 2,
                0, 0
            },
            {
                0, 0, 0, 1, 0, 0, 0, 0, 1, 1,
                0, 0, 0, 0, 0, 1, 2, 2, 0, 0,
                0, 0, 0, 2, 0, 0, 0, 0, 0, 2,
                0, 0
            },
            {
                0, 0, 0, 1, 1, 0, 0, 0, 1, 1,
                0, 0, 0, 0, 0, 1, 2, 2, 0, 0,
                0, 0, 0, 2, 0, 0, 0, 0, 2, 2,
                0, 0
            },
            {
                0, 0, 0, 1, 1, 0, 0, 0, 1, 1,
                0, 0, 0, 0, 0, 1, 2, 2, 0, 0,
                0, 0, 0, 2, 0, 0, 0, 0, 2, 2,
                0, 0
            },
            {
                0, 0, 0, 1, 1, 0, 0, 0, 1, 1,
                0, 0, 0, 0, 0, 1, 2, 2, 0, 0,
                0, 0, 0, 2, 0, 0, 0, 0, 2, 2,
                0, 0
            },
            {
                0, 0, 0, 1, 1, 1, 0, 0, 1, 1,
                0, 0, 0, 0, 0, 1, 2, 2, 0, 0,
                0, 0, 0, 2, 0, 0, 0, 2, 2, 2,
                0, 0
            },
            {
                0, 0, 0, 1, 1, 1, 0, 0, 1, 1,
                0, 0, 0, 0, 0, 1, 2, 2, 0, 0,
                0, 0, 0, 2, 0, 0, 0, 2, 2, 2,
                0, 0
            }
        };
        mapUserColor();
        byte[,] array6 = new byte[3, 4]
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
        int num = array6[0, fans - 1] / 2;
        int num2 = array6[1, fans - 1] / 2;
        int num3 = 0;
        for (int i = 0; i < 2; i++)
        {
            int num4 = 0;
            int num5 = 0;
            while (num5 < num + fans + fans - 1)
            {
                for (int j = 0; j < num; j++)
                {
                    byte b = 0;
                    byte b2 = 0;
                    byte b3 = 0;
                    int num6 = num5;
                    int num7 = j;
                    if (num5 >= num)
                    {
                        num6 = 0;
                        num7 = 0;
                    }
                    byte b4;
                    byte b5;
                    switch (fans)
                    {
                    case 1:
                        b4 = 0;
                        b5 = 0;
                        break;
                    case 2:
                        b4 = array4[num6, num7 + 8];
                        b5 = array4[7, j + 8];
                        break;
                    case 3:
                        b4 = array2[num6, num7];
                        b5 = array2[11, j];
                        break;
                    default:
                        b4 = array4[num6, num7];
                        b5 = array4[15, j];
                        break;
                    }
                    if (pn == 1)
                    {
                        switch (fans)
                        {
                        case 1:
                            b4 = 0;
                            b5 = 0;
                            break;
                        case 2:
                            b4 = array5[num6, num7 + 8];
                            b5 = array5[7, j + 8];
                            break;
                        case 3:
                            b4 = array3[num6, num7];
                            b5 = array3[11, j];
                            break;
                        default:
                            b4 = array5[num6, num7];
                            b5 = array5[15, j];
                            break;
                        }
                    }
                    if ((num5 < num && b4 != 0) || (num5 >= num && num5 < num + fans - 1 && b5 != 0))
                    {
                        b = palette[2, i * 2, 0];
                        b2 = palette[2, i * 2, 1];
                        b3 = palette[2, i * 2, 2];
                    }
                    array[0, 0, j] = (byte)(b * bright >> 8);
                    array[0, 1, j] = (byte)(b2 * bright >> 8);
                    array[0, 2, j] = (byte)(b3 * bright >> 8);
                }
                for (int j = 0; j < num; j++)
                {
                    byte b = 0;
                    byte b2 = 0;
                    byte b3 = 0;
                    int num6 = num5;
                    int num7 = j;
                    if (num5 >= num)
                    {
                        num6 = 0;
                        num7 = 0;
                    }
                    byte b4;
                    byte b5;
                    switch (fans)
                    {
                    case 1:
                        b4 = 0;
                        b5 = 0;
                        break;
                    case 2:
                        b4 = array4[num6, num7 + 8 + 8];
                        b5 = array4[7, j + 8 + 8];
                        break;
                    case 3:
                        b4 = array2[num6, num7 + 12];
                        b5 = array2[11, j + 12];
                        break;
                    default:
                        b4 = array4[num6, num7 + 16];
                        b5 = array4[15, j + 16];
                        break;
                    }
                    if (pn == 1)
                    {
                        switch (fans)
                        {
                        case 1:
                            b4 = 0;
                            b5 = 0;
                            break;
                        case 2:
                            b4 = array5[num6, num7 + 8 + 8];
                            b5 = array5[7, j + 8 + 8];
                            break;
                        case 3:
                            b4 = array3[num6, num7 + 12];
                            b5 = array3[11, j + 12];
                            break;
                        default:
                            b4 = array5[num6, num7 + 16];
                            b5 = array5[15, j + 16];
                            break;
                        }
                    }
                    if ((num5 < num && b4 != 0) || (num5 >= num && num5 < num + fans - 1 && b5 != 0))
                    {
                        b = palette[2, i * 2 + 1, 0];
                        b2 = palette[2, i * 2 + 1, 1];
                        b3 = palette[2, i * 2 + 1, 2];
                    }
                    array[0, 0, j + num] = (byte)(b * bright >> 8);
                    array[0, 1, j + num] = (byte)(b2 * bright >> 8);
                    array[0, 2, j + num] = (byte)(b3 * bright >> 8);
                }
                if ((fans == 1 || fans == 3) && num5 < num + fans - 1)
                {
                    byte b = ((palette[2, i * 2, 0] + palette[2, i * 2 + 1, 0] <= 255) ? ((byte)(palette[2, i * 2, 0] + palette[2, i * 2 + 1, 0])) : byte.MaxValue);
                    byte b2 = ((palette[2, i * 2, 1] + palette[2, i * 2 + 1, 1] <= 255) ? ((byte)(palette[2, i * 2, 1] + palette[2, i * 2 + 1, 1])) : byte.MaxValue);
                    byte b3 = ((palette[2, i * 2, 2] + palette[2, i * 2 + 1, 2] <= 255) ? ((byte)(palette[2, i * 2, 2] + palette[2, i * 2 + 1, 2])) : byte.MaxValue);
                    if (fans == 3)
                    {
                        if (pn == 0)
                        {
                            array[0, 0, 12] = (byte)(b * bright >> 8);
                            array[0, 1, 12] = (byte)(b2 * bright >> 8);
                            array[0, 2, 12] = (byte)(b3 * bright >> 8);
                        }
                        else
                        {
                            array[0, 0, 8] = (byte)(b * bright >> 8);
                            array[0, 1, 8] = (byte)(b2 * bright >> 8);
                            array[0, 2, 8] = (byte)(b3 * bright >> 8);
                        }
                    }
                    else if (pn == 0)
                    {
                        array[0, 0, 4] = (byte)(b * bright >> 8);
                        array[0, 1, 4] = (byte)(b2 * bright >> 8);
                        array[0, 2, 4] = (byte)(b3 * bright >> 8);
                    }
                    else
                    {
                        array[0, 0, 0] = (byte)(b * bright >> 8);
                        array[0, 1, 0] = (byte)(b2 * bright >> 8);
                        array[0, 2, 0] = (byte)(b3 * bright >> 8);
                    }
                    if (num5 > 2)
                    {
                        b = palette[2, i * 2, 0];
                        b2 = palette[2, i * 2, 1];
                        b3 = palette[2, i * 2, 2];
                        if (fans == 3)
                        {
                            if (pn == 0)
                            {
                                array[0, 0, 11] = (byte)(b * bright >> 8);
                                array[0, 1, 11] = (byte)(b2 * bright >> 8);
                                array[0, 2, 11] = (byte)(b3 * bright >> 8);
                            }
                            else
                            {
                                array[0, 0, 15] = (byte)(b * bright >> 8);
                                array[0, 1, 15] = (byte)(b2 * bright >> 8);
                                array[0, 2, 15] = (byte)(b3 * bright >> 8);
                                b = palette[2, i * 2 + 1, 0];
                                b2 = palette[2, i * 2 + 1, 1];
                                b3 = palette[2, i * 2 + 1, 2];
                                array[0, 0, 9] = (byte)(b * bright >> 8);
                                array[0, 1, 9] = (byte)(b2 * bright >> 8);
                                array[0, 2, 9] = (byte)(b3 * bright >> 8);
                            }
                        }
                        else if (pn == 0)
                        {
                            array[0, 0, 3] = (byte)(b * bright >> 8);
                            array[0, 1, 3] = (byte)(b2 * bright >> 8);
                            array[0, 2, 3] = (byte)(b3 * bright >> 8);
                            b = palette[2, i * 2 + 1, 0];
                            b2 = palette[2, i * 2 + 1, 1];
                            b3 = palette[2, i * 2 + 1, 2];
                            array[0, 0, 5] = (byte)(b * bright >> 8);
                            array[0, 1, 5] = (byte)(b2 * bright >> 8);
                            array[0, 2, 5] = (byte)(b3 * bright >> 8);
                        }
                        else
                        {
                            array[0, 0, 7] = (byte)(b * bright >> 8);
                            array[0, 1, 7] = (byte)(b2 * bright >> 8);
                            array[0, 2, 7] = (byte)(b3 * bright >> 8);
                            b = palette[2, i * 2 + 1, 0];
                            b2 = palette[2, i * 2 + 1, 1];
                            b3 = palette[2, i * 2 + 1, 2];
                            array[0, 0, 1] = (byte)(b * bright >> 8);
                            array[0, 1, 1] = (byte)(b2 * bright >> 8);
                            array[0, 2, 1] = (byte)(b3 * bright >> 8);
                        }
                    }
                }
                for (int j = 0; j < num; j++)
                {
                    byte b = 0;
                    byte b2 = 0;
                    byte b3 = 0;
                    if (j <= num5 && j + fans > num5)
                    {
                        b = palette[0, i * 2, 0];
                        b2 = palette[0, i * 2, 1];
                        b3 = palette[0, i * 2, 2];
                    }
                    int num6 = num - j - 1;
                    array[2, 0, num6] = (byte)(b * bright >> 8);
                    array[2, 1, num6] = (byte)(b2 * bright >> 8);
                    array[2, 2, num6] = (byte)(b3 * bright >> 8);
                    array[4, 0, num6] = array[2, 0, num6];
                    array[4, 1, num6] = array[2, 1, num6];
                    array[4, 2, num6] = array[2, 2, num6];
                }
                for (int j = 0; j < num; j++)
                {
                    byte b = 0;
                    byte b2 = 0;
                    byte b3 = 0;
                    if (j <= num5 && j + fans > num5)
                    {
                        b = palette[0, i * 2 + 1, 0];
                        b2 = palette[0, i * 2 + 1, 1];
                        b3 = palette[0, i * 2 + 1, 2];
                    }
                    array[2, 0, j + num] = (byte)(b * bright >> 8);
                    array[2, 1, j + num] = (byte)(b2 * bright >> 8);
                    array[2, 2, j + num] = (byte)(b3 * bright >> 8);
                    array[4, 0, j + num] = array[2, 0, j + num];
                    array[4, 1, j + num] = array[2, 1, j + num];
                    array[4, 2, j + num] = array[2, 2, j + num];
                }
                if (num5 == 2 || num5 == 6 || num5 == 10 || num5 == 14)
                {
                    num4++;
                }
                for (int j = 0; j < num2; j++)
                {
                    byte b = 0;
                    byte b2 = 0;
                    byte b3 = 0;
                    if (j <= num4 && j + fans > num4)
                    {
                        b = palette[1, i * 2, 0];
                        b2 = palette[1, i * 2, 1];
                        b3 = palette[1, i * 2, 2];
                    }
                    int num6 = num2 - j - 1;
                    array[1, 0, num6] = (byte)(b * bright >> 8);
                    array[1, 1, num6] = (byte)(b2 * bright >> 8);
                    array[1, 2, num6] = (byte)(b3 * bright >> 8);
                    array[3, 0, num6] = array[1, 0, num6];
                    array[3, 1, num6] = array[1, 1, num6];
                    array[3, 2, num6] = array[1, 2, num6];
                }
                for (int j = 0; j < num2; j++)
                {
                    byte b = 0;
                    byte b2 = 0;
                    byte b3 = 0;
                    if (j <= num4 && j + fans > num4)
                    {
                        b = palette[1, i * 2 + 1, 0];
                        b2 = palette[1, i * 2 + 1, 1];
                        b3 = palette[1, i * 2 + 1, 2];
                    }
                    array[1, 0, j + num2] = (byte)(b * bright >> 8);
                    array[1, 1, j + num2] = (byte)(b2 * bright >> 8);
                    array[1, 2, j + num2] = (byte)(b3 * bright >> 8);
                    array[3, 0, j + num2] = array[1, 0, j + num2];
                    array[3, 1, j + num2] = array[1, 1, j + num2];
                    array[3, 2, j + num2] = array[1, 2, j + num2];
                }
                int num8 = num3;
                for (int k = 0; k < fans; k++)
                {
                    int num7 = k * 44;
                    int num6 = k * 8;
                    for (int j = num7; j < num7 + 8; j++)
                    {
                        frames[num8, 0, j] = 0;
                        frames[num8, 1, j] = 0;
                        frames[num8, 2, j] = 0;
                        if (ie == 2)
                        {
                            frames[num8, 0, j] = array[0, 0, num6];
                            frames[num8, 1, j] = array[0, 1, num6];
                            frames[num8, 2, j] = array[0, 2, num6];
                            num6++;
                        }
                    }
                    num6 = k * 10;
                    if (pn == 0)
                    {
                        num6 = num6 + 10 - 1;
                    }
                    for (int j = num7 + 8; j < num7 + 18; j++)
                    {
                        frames[num8, 0, j] = 0;
                        frames[num8, 1, j] = 0;
                        frames[num8, 2, j] = 0;
                        if (ie == 1)
                        {
                            frames[num8, 0, j] = array[1, 0, num6];
                            frames[num8, 1, j] = array[1, 1, num6];
                            frames[num8, 2, j] = array[1, 2, num6];
                            num6 = ((pn != 1) ? (num6 - 1) : (num6 + 1));
                        }
                    }
                    num6 = k * 8;
                    if (pn == 0)
                    {
                        num6 = num6 + 8 - 1;
                    }
                    for (int j = num7 + 18; j < num7 + 26; j++)
                    {
                        frames[num8, 0, j] = 0;
                        frames[num8, 1, j] = 0;
                        frames[num8, 2, j] = 0;
                        if (ie == 0)
                        {
                            frames[num8, 0, j] = array[2, 0, num6];
                            frames[num8, 1, j] = array[2, 1, num6];
                            frames[num8, 2, j] = array[2, 2, num6];
                            num6 = ((pn != 1) ? (num6 - 1) : (num6 + 1));
                        }
                    }
                    num6 = k * 10;
                    if (pn == 0)
                    {
                        num6 = num6 + 10 - 1;
                    }
                    for (int j = num7 + 26; j < num7 + 36; j++)
                    {
                        frames[num8, 0, j] = 0;
                        frames[num8, 1, j] = 0;
                        frames[num8, 2, j] = 0;
                        if (ie == 1)
                        {
                            frames[num8, 0, j] = array[3, 0, num6];
                            frames[num8, 1, j] = array[3, 1, num6];
                            frames[num8, 2, j] = array[3, 2, num6];
                            num6 = ((pn != 1) ? (num6 - 1) : (num6 + 1));
                        }
                    }
                    num6 = k * 8;
                    if (pn == 0)
                    {
                        num6 = num6 + 8 - 1;
                    }
                    for (int j = num7 + 36; j < num7 + 44; j++)
                    {
                        frames[num8, 0, j] = 0;
                        frames[num8, 1, j] = 0;
                        frames[num8, 2, j] = 0;
                        if (ie == 0)
                        {
                            frames[num8, 0, j] = array[4, 0, num6];
                            frames[num8, 1, j] = array[4, 1, num6];
                            frames[num8, 2, j] = array[4, 2, num6];
                            num6 = ((pn != 1) ? (num6 - 1) : (num6 + 1));
                        }
                    }
                }
                num3++;
                num5++;
                num4++;
            }
        }
        return num3;
    }
    internal int InfDrumming(int ie, int fans, int bright, int direction, int pn)
    {
        byte[,,] array = new byte[5, 3, 40];
        byte[,] array2 = new byte[12, 4]
        {
            { 0, 0, 0, 0 },
            { 0, 0, 0, 0 },
            { 1, 1, 1, 1 },
            { 1, 1, 1, 1 },
            { 1, 1, 1, 1 },
            { 1, 1, 1, 1 },
            { 0, 0, 0, 0 },
            { 0, 0, 0, 0 },
            { 0, 1, 1, 0 },
            { 0, 1, 1, 0 },
            { 1, 0, 0, 1 },
            { 1, 0, 0, 1 }
        };
        byte[,] array3 = new byte[108, 4]
        {
            { 51, 0, 0, 51 },
            { 102, 0, 0, 102 },
            { 153, 0, 0, 153 },
            { 204, 0, 0, 204 },
            { 255, 0, 0, 255 },
            { 204, 0, 0, 204 },
            { 153, 0, 0, 153 },
            { 102, 0, 0, 102 },
            { 51, 0, 0, 51 },
            { 51, 0, 0, 51 },
            { 102, 0, 0, 102 },
            { 153, 0, 0, 153 },
            { 204, 0, 0, 204 },
            { 255, 0, 0, 255 },
            { 204, 0, 0, 204 },
            { 153, 0, 0, 153 },
            { 102, 0, 0, 102 },
            { 51, 0, 0, 51 },
            { 0, 51, 51, 0 },
            { 0, 102, 102, 0 },
            { 0, 153, 153, 0 },
            { 0, 204, 204, 0 },
            { 0, 255, 255, 0 },
            { 0, 204, 204, 0 },
            { 0, 153, 153, 0 },
            { 0, 102, 102, 0 },
            { 0, 51, 51, 0 },
            { 0, 51, 51, 0 },
            { 0, 102, 102, 0 },
            { 0, 153, 153, 0 },
            { 0, 204, 204, 0 },
            { 0, 255, 255, 0 },
            { 0, 204, 204, 0 },
            { 0, 153, 153, 0 },
            { 0, 102, 102, 0 },
            { 0, 51, 51, 0 },
            { 51, 0, 0, 51 },
            { 102, 0, 0, 102 },
            { 153, 0, 0, 153 },
            { 204, 0, 0, 204 },
            { 255, 0, 0, 255 },
            { 204, 0, 0, 204 },
            { 153, 0, 0, 153 },
            { 102, 0, 0, 102 },
            { 51, 0, 0, 51 },
            { 51, 0, 0, 51 },
            { 102, 0, 0, 102 },
            { 153, 0, 0, 153 },
            { 204, 0, 0, 204 },
            { 255, 0, 0, 255 },
            { 204, 0, 0, 204 },
            { 153, 0, 0, 153 },
            { 102, 0, 0, 102 },
            { 51, 0, 0, 51 },
            { 0, 51, 51, 0 },
            { 0, 102, 102, 0 },
            { 0, 153, 153, 0 },
            { 0, 204, 204, 0 },
            { 0, 255, 255, 0 },
            { 0, 204, 204, 0 },
            { 0, 153, 153, 0 },
            { 0, 102, 102, 0 },
            { 0, 51, 51, 0 },
            { 0, 51, 51, 0 },
            { 0, 102, 102, 0 },
            { 0, 153, 153, 0 },
            { 0, 204, 204, 0 },
            { 0, 255, 255, 0 },
            { 0, 204, 204, 0 },
            { 0, 153, 153, 0 },
            { 0, 102, 102, 0 },
            { 0, 51, 51, 0 },
            { 51, 51, 51, 51 },
            { 102, 102, 102, 102 },
            { 153, 153, 153, 153 },
            { 204, 204, 204, 204 },
            { 255, 255, 255, 255 },
            { 204, 204, 204, 204 },
            { 153, 153, 153, 153 },
            { 102, 102, 102, 102 },
            { 51, 51, 51, 51 },
            { 51, 51, 51, 51 },
            { 102, 102, 102, 102 },
            { 153, 153, 153, 153 },
            { 204, 204, 204, 204 },
            { 255, 255, 255, 255 },
            { 204, 204, 204, 204 },
            { 153, 153, 153, 153 },
            { 102, 102, 102, 102 },
            { 51, 51, 51, 51 },
            { 51, 51, 51, 51 },
            { 102, 102, 102, 102 },
            { 153, 153, 153, 153 },
            { 204, 204, 204, 204 },
            { 255, 255, 255, 255 },
            { 204, 204, 204, 204 },
            { 153, 153, 153, 153 },
            { 102, 102, 102, 102 },
            { 51, 51, 51, 51 },
            { 51, 51, 51, 51 },
            { 102, 102, 102, 102 },
            { 153, 153, 153, 153 },
            { 204, 204, 204, 204 },
            { 255, 255, 255, 255 },
            { 204, 204, 204, 204 },
            { 153, 153, 153, 153 },
            { 102, 102, 102, 102 },
            { 51, 51, 51, 51 }
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
        int num = 0;
        for (int i = 0; i < 108; i++)
        {
            byte b = 0;
            for (int j = 0; j < 4; j++)
            {
                byte b2 = array2[i / 9, j];
                byte b3 = array3[i, j];
                byte b4 = (byte)(palette[3, b2, 0] * b3 >> 8);
                byte b5 = (byte)(palette[3, b2, 1] * b3 >> 8);
                byte b6 = (byte)(palette[3, b2, 2] * b3 >> 8);
                int num2 = (j - b) * 8;
                for (int k = 0; k < 8; k++)
                {
                    array[0, 0, num2 + k] = (byte)(b4 * bright >> 8);
                    array[0, 1, num2 + k] = (byte)(b5 * bright >> 8);
                    array[0, 2, num2 + k] = (byte)(b6 * bright >> 8);
                }
                num2 = (j - b) * 10;
                for (int k = 0; k < 10; k++)
                {
                    array[1, 0, num2 + k] = (byte)(b4 * bright >> 8);
                    array[1, 1, num2 + k] = (byte)(b5 * bright >> 8);
                    array[1, 2, num2 + k] = (byte)(b6 * bright >> 8);
                    array[3, 0, num2 + k] = array[1, 0, num2 + k];
                    array[3, 1, num2 + k] = array[1, 1, num2 + k];
                    array[3, 2, num2 + k] = array[1, 2, num2 + k];
                }
                num2 = (j - b) * 8;
                b3 = array3[i % 9, 0];
                b4 = (byte)(palette[3, b2, 0] * b3 >> 8);
                b5 = (byte)(palette[3, b2, 1] * b3 >> 8);
                b6 = (byte)(palette[3, b2, 2] * b3 >> 8);
                for (int k = 0; k < 8; k++)
                {
                    array[2, 0, num2 + k] = (byte)(b4 * bright >> 8);
                    array[2, 1, num2 + k] = (byte)(b5 * bright >> 8);
                    array[2, 2, num2 + k] = (byte)(b6 * bright >> 8);
                    array[4, 0, num2 + k] = array[2, 0, num2 + k];
                    array[4, 1, num2 + k] = array[2, 1, num2 + k];
                    array[4, 2, num2 + k] = array[2, 2, num2 + k];
                }
                if (fans == 3 && j == 1)
                {
                    j = 2;
                    b = 1;
                }
                if (fans == 2 && j == 1)
                {
                    j = 3;
                    b = 2;
                }
                if (fans == 1 && j == 0)
                {
                    j = 3;
                    b = 3;
                }
            }
            int num3 = num;
            for (int j = 0; j < fans; j++)
            {
                int num4 = j * 44;
                int num2 = j * 8;
                if (pn == 0)
                {
                    num2 = num2 + 8 - 1;
                }
                for (int k = num4; k < num4 + 8; k++)
                {
                    frames[num3, 0, k] = 0;
                    frames[num3, 1, k] = 0;
                    frames[num3, 2, k] = 0;
                    if (ie == 2)
                    {
                        frames[num3, 0, k] = array[0, 0, num2];
                        frames[num3, 1, k] = array[0, 1, num2];
                        frames[num3, 2, k] = array[0, 2, num2];
                        num2 = ((pn != 1) ? (num2 - 1) : (num2 + 1));
                    }
                }
                num2 = j * 10;
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
                        frames[num3, 0, k] = array[1, 0, num2];
                        frames[num3, 1, k] = array[1, 1, num2];
                        frames[num3, 2, k] = array[1, 2, num2];
                        num2 = ((pn != 1) ? (num2 - 1) : (num2 + 1));
                    }
                }
                num2 = j * 8;
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
                        frames[num3, 0, k] = array[2, 0, num2];
                        frames[num3, 1, k] = array[2, 1, num2];
                        frames[num3, 2, k] = array[2, 2, num2];
                        num2 = ((pn != 1) ? (num2 - 1) : (num2 + 1));
                    }
                }
                num2 = j * 10;
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
                        frames[num3, 0, k] = array[3, 0, num2];
                        frames[num3, 1, k] = array[3, 1, num2];
                        frames[num3, 2, k] = array[3, 2, num2];
                        num2 = ((pn != 1) ? (num2 - 1) : (num2 + 1));
                    }
                }
                num2 = j * 8;
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
                        frames[num3, 0, k] = array[4, 0, num2];
                        frames[num3, 1, k] = array[4, 1, num2];
                        frames[num3, 2, k] = array[4, 2, num2];
                        num2 = ((pn != 1) ? (num2 - 1) : (num2 + 1));
                    }
                }
            }
            num++;
        }
        return num;
    }
}
