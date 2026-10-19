using System;

namespace Nexus.Service.Peripherals.LianLiFlex;

internal sealed partial class FlexSlInfModes
{
    internal int InfWing(int zone, int fans, int bright, int direction, int attach)
    {
        byte[,,] ringColors = new byte[5, 3, 40];
        byte[,] wingMask3 = new byte[12, 24]
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
        byte[,] wingMask3RightAttach = new byte[12, 24]
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
        byte[,] wingMask4 = new byte[16, 32]
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
        byte[,] wingMask4RightAttach = new byte[16, 32]
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
        int centerOuterHalf = zoneLedCountByFans[0, fans - 1] / 2;
        int innerHalf = zoneLedCountByFans[1, fans - 1] / 2;
        int frame = 0;
        for (int side = 0; side < 2; side++)
        {
            int innerHead = 0;
            int head = 0;
            while (head < centerOuterHalf + fans + fans - 1)
            {
                for (int led = 0; led < centerOuterHalf; led++)
                {
                    byte red = 0;
                    byte green = 0;
                    byte blue = 0;
                    int maskRow = head;
                    int maskCol = led;
                    if (head >= centerOuterHalf)
                    {
                        maskRow = 0;
                        maskCol = 0;
                    }
                    byte bodyMask;
                    byte tailMask;
                    switch (fans)
                    {
                    case 1:
                        bodyMask = 0;
                        tailMask = 0;
                        break;
                    case 2:
                        bodyMask = wingMask4[maskRow, maskCol + 8];
                        tailMask = wingMask4[7, led + 8];
                        break;
                    case 3:
                        bodyMask = wingMask3[maskRow, maskCol];
                        tailMask = wingMask3[11, led];
                        break;
                    default:
                        bodyMask = wingMask4[maskRow, maskCol];
                        tailMask = wingMask4[15, led];
                        break;
                    }
                    if (attach == 1)
                    {
                        switch (fans)
                        {
                        case 1:
                            bodyMask = 0;
                            tailMask = 0;
                            break;
                        case 2:
                            bodyMask = wingMask4RightAttach[maskRow, maskCol + 8];
                            tailMask = wingMask4RightAttach[7, led + 8];
                            break;
                        case 3:
                            bodyMask = wingMask3RightAttach[maskRow, maskCol];
                            tailMask = wingMask3RightAttach[11, led];
                            break;
                        default:
                            bodyMask = wingMask4RightAttach[maskRow, maskCol];
                            tailMask = wingMask4RightAttach[15, led];
                            break;
                        }
                    }
                    if ((head < centerOuterHalf && bodyMask != 0) || (head >= centerOuterHalf && head < centerOuterHalf + fans - 1 && tailMask != 0))
                    {
                        red = palette[2, side * 2, 0];
                        green = palette[2, side * 2, 1];
                        blue = palette[2, side * 2, 2];
                    }
                    ringColors[0, 0, led] = (byte)(red * bright >> 8);
                    ringColors[0, 1, led] = (byte)(green * bright >> 8);
                    ringColors[0, 2, led] = (byte)(blue * bright >> 8);
                }
                for (int led = 0; led < centerOuterHalf; led++)
                {
                    byte red = 0;
                    byte green = 0;
                    byte blue = 0;
                    int maskRow = head;
                    int maskCol = led;
                    if (head >= centerOuterHalf)
                    {
                        maskRow = 0;
                        maskCol = 0;
                    }
                    byte bodyMask;
                    byte tailMask;
                    switch (fans)
                    {
                    case 1:
                        bodyMask = 0;
                        tailMask = 0;
                        break;
                    case 2:
                        bodyMask = wingMask4[maskRow, maskCol + 8 + 8];
                        tailMask = wingMask4[7, led + 8 + 8];
                        break;
                    case 3:
                        bodyMask = wingMask3[maskRow, maskCol + 12];
                        tailMask = wingMask3[11, led + 12];
                        break;
                    default:
                        bodyMask = wingMask4[maskRow, maskCol + 16];
                        tailMask = wingMask4[15, led + 16];
                        break;
                    }
                    if (attach == 1)
                    {
                        switch (fans)
                        {
                        case 1:
                            bodyMask = 0;
                            tailMask = 0;
                            break;
                        case 2:
                            bodyMask = wingMask4RightAttach[maskRow, maskCol + 8 + 8];
                            tailMask = wingMask4RightAttach[7, led + 8 + 8];
                            break;
                        case 3:
                            bodyMask = wingMask3RightAttach[maskRow, maskCol + 12];
                            tailMask = wingMask3RightAttach[11, led + 12];
                            break;
                        default:
                            bodyMask = wingMask4RightAttach[maskRow, maskCol + 16];
                            tailMask = wingMask4RightAttach[15, led + 16];
                            break;
                        }
                    }
                    if ((head < centerOuterHalf && bodyMask != 0) || (head >= centerOuterHalf && head < centerOuterHalf + fans - 1 && tailMask != 0))
                    {
                        red = palette[2, side * 2 + 1, 0];
                        green = palette[2, side * 2 + 1, 1];
                        blue = palette[2, side * 2 + 1, 2];
                    }
                    ringColors[0, 0, led + centerOuterHalf] = (byte)(red * bright >> 8);
                    ringColors[0, 1, led + centerOuterHalf] = (byte)(green * bright >> 8);
                    ringColors[0, 2, led + centerOuterHalf] = (byte)(blue * bright >> 8);
                }
                if ((fans == 1 || fans == 3) && head < centerOuterHalf + fans - 1)
                {
                    byte red = ((palette[2, side * 2, 0] + palette[2, side * 2 + 1, 0] <= 255) ? ((byte)(palette[2, side * 2, 0] + palette[2, side * 2 + 1, 0])) : byte.MaxValue);
                    byte green = ((palette[2, side * 2, 1] + palette[2, side * 2 + 1, 1] <= 255) ? ((byte)(palette[2, side * 2, 1] + palette[2, side * 2 + 1, 1])) : byte.MaxValue);
                    byte blue = ((palette[2, side * 2, 2] + palette[2, side * 2 + 1, 2] <= 255) ? ((byte)(palette[2, side * 2, 2] + palette[2, side * 2 + 1, 2])) : byte.MaxValue);
                    if (fans == 3)
                    {
                        if (attach == 0)
                        {
                            ringColors[0, 0, 12] = (byte)(red * bright >> 8);
                            ringColors[0, 1, 12] = (byte)(green * bright >> 8);
                            ringColors[0, 2, 12] = (byte)(blue * bright >> 8);
                        }
                        else
                        {
                            ringColors[0, 0, 8] = (byte)(red * bright >> 8);
                            ringColors[0, 1, 8] = (byte)(green * bright >> 8);
                            ringColors[0, 2, 8] = (byte)(blue * bright >> 8);
                        }
                    }
                    else if (attach == 0)
                    {
                        ringColors[0, 0, 4] = (byte)(red * bright >> 8);
                        ringColors[0, 1, 4] = (byte)(green * bright >> 8);
                        ringColors[0, 2, 4] = (byte)(blue * bright >> 8);
                    }
                    else
                    {
                        ringColors[0, 0, 0] = (byte)(red * bright >> 8);
                        ringColors[0, 1, 0] = (byte)(green * bright >> 8);
                        ringColors[0, 2, 0] = (byte)(blue * bright >> 8);
                    }
                    if (head > 2)
                    {
                        red = palette[2, side * 2, 0];
                        green = palette[2, side * 2, 1];
                        blue = palette[2, side * 2, 2];
                        if (fans == 3)
                        {
                            if (attach == 0)
                            {
                                ringColors[0, 0, 11] = (byte)(red * bright >> 8);
                                ringColors[0, 1, 11] = (byte)(green * bright >> 8);
                                ringColors[0, 2, 11] = (byte)(blue * bright >> 8);
                            }
                            else
                            {
                                ringColors[0, 0, 15] = (byte)(red * bright >> 8);
                                ringColors[0, 1, 15] = (byte)(green * bright >> 8);
                                ringColors[0, 2, 15] = (byte)(blue * bright >> 8);
                                red = palette[2, side * 2 + 1, 0];
                                green = palette[2, side * 2 + 1, 1];
                                blue = palette[2, side * 2 + 1, 2];
                                ringColors[0, 0, 9] = (byte)(red * bright >> 8);
                                ringColors[0, 1, 9] = (byte)(green * bright >> 8);
                                ringColors[0, 2, 9] = (byte)(blue * bright >> 8);
                            }
                        }
                        else if (attach == 0)
                        {
                            ringColors[0, 0, 3] = (byte)(red * bright >> 8);
                            ringColors[0, 1, 3] = (byte)(green * bright >> 8);
                            ringColors[0, 2, 3] = (byte)(blue * bright >> 8);
                            red = palette[2, side * 2 + 1, 0];
                            green = palette[2, side * 2 + 1, 1];
                            blue = palette[2, side * 2 + 1, 2];
                            ringColors[0, 0, 5] = (byte)(red * bright >> 8);
                            ringColors[0, 1, 5] = (byte)(green * bright >> 8);
                            ringColors[0, 2, 5] = (byte)(blue * bright >> 8);
                        }
                        else
                        {
                            ringColors[0, 0, 7] = (byte)(red * bright >> 8);
                            ringColors[0, 1, 7] = (byte)(green * bright >> 8);
                            ringColors[0, 2, 7] = (byte)(blue * bright >> 8);
                            red = palette[2, side * 2 + 1, 0];
                            green = palette[2, side * 2 + 1, 1];
                            blue = palette[2, side * 2 + 1, 2];
                            ringColors[0, 0, 1] = (byte)(red * bright >> 8);
                            ringColors[0, 1, 1] = (byte)(green * bright >> 8);
                            ringColors[0, 2, 1] = (byte)(blue * bright >> 8);
                        }
                    }
                }
                for (int led = 0; led < centerOuterHalf; led++)
                {
                    byte red = 0;
                    byte green = 0;
                    byte blue = 0;
                    if (led <= head && led + fans > head)
                    {
                        red = palette[0, side * 2, 0];
                        green = palette[0, side * 2, 1];
                        blue = palette[0, side * 2, 2];
                    }
                    int mirrorIdx = centerOuterHalf - led - 1;
                    ringColors[2, 0, mirrorIdx] = (byte)(red * bright >> 8);
                    ringColors[2, 1, mirrorIdx] = (byte)(green * bright >> 8);
                    ringColors[2, 2, mirrorIdx] = (byte)(blue * bright >> 8);
                    ringColors[4, 0, mirrorIdx] = ringColors[2, 0, mirrorIdx];
                    ringColors[4, 1, mirrorIdx] = ringColors[2, 1, mirrorIdx];
                    ringColors[4, 2, mirrorIdx] = ringColors[2, 2, mirrorIdx];
                }
                for (int led = 0; led < centerOuterHalf; led++)
                {
                    byte red = 0;
                    byte green = 0;
                    byte blue = 0;
                    if (led <= head && led + fans > head)
                    {
                        red = palette[0, side * 2 + 1, 0];
                        green = palette[0, side * 2 + 1, 1];
                        blue = palette[0, side * 2 + 1, 2];
                    }
                    ringColors[2, 0, led + centerOuterHalf] = (byte)(red * bright >> 8);
                    ringColors[2, 1, led + centerOuterHalf] = (byte)(green * bright >> 8);
                    ringColors[2, 2, led + centerOuterHalf] = (byte)(blue * bright >> 8);
                    ringColors[4, 0, led + centerOuterHalf] = ringColors[2, 0, led + centerOuterHalf];
                    ringColors[4, 1, led + centerOuterHalf] = ringColors[2, 1, led + centerOuterHalf];
                    ringColors[4, 2, led + centerOuterHalf] = ringColors[2, 2, led + centerOuterHalf];
                }
                if (head == 2 || head == 6 || head == 10 || head == 14)
                {
                    innerHead++;
                }
                for (int led = 0; led < innerHalf; led++)
                {
                    byte red = 0;
                    byte green = 0;
                    byte blue = 0;
                    if (led <= innerHead && led + fans > innerHead)
                    {
                        red = palette[1, side * 2, 0];
                        green = palette[1, side * 2, 1];
                        blue = palette[1, side * 2, 2];
                    }
                    int mirrorIdx = innerHalf - led - 1;
                    ringColors[1, 0, mirrorIdx] = (byte)(red * bright >> 8);
                    ringColors[1, 1, mirrorIdx] = (byte)(green * bright >> 8);
                    ringColors[1, 2, mirrorIdx] = (byte)(blue * bright >> 8);
                    ringColors[3, 0, mirrorIdx] = ringColors[1, 0, mirrorIdx];
                    ringColors[3, 1, mirrorIdx] = ringColors[1, 1, mirrorIdx];
                    ringColors[3, 2, mirrorIdx] = ringColors[1, 2, mirrorIdx];
                }
                for (int led = 0; led < innerHalf; led++)
                {
                    byte red = 0;
                    byte green = 0;
                    byte blue = 0;
                    if (led <= innerHead && led + fans > innerHead)
                    {
                        red = palette[1, side * 2 + 1, 0];
                        green = palette[1, side * 2 + 1, 1];
                        blue = palette[1, side * 2 + 1, 2];
                    }
                    ringColors[1, 0, led + innerHalf] = (byte)(red * bright >> 8);
                    ringColors[1, 1, led + innerHalf] = (byte)(green * bright >> 8);
                    ringColors[1, 2, led + innerHalf] = (byte)(blue * bright >> 8);
                    ringColors[3, 0, led + innerHalf] = ringColors[1, 0, led + innerHalf];
                    ringColors[3, 1, led + innerHalf] = ringColors[1, 1, led + innerHalf];
                    ringColors[3, 2, led + innerHalf] = ringColors[1, 2, led + innerHalf];
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
                            frames[frameOut, 0, led] = ringColors[0, 0, ringIdx];
                            frames[frameOut, 1, led] = ringColors[0, 1, ringIdx];
                            frames[frameOut, 2, led] = ringColors[0, 2, ringIdx];
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
                            frames[frameOut, 0, led] = ringColors[1, 0, ringIdx];
                            frames[frameOut, 1, led] = ringColors[1, 1, ringIdx];
                            frames[frameOut, 2, led] = ringColors[1, 2, ringIdx];
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
                            frames[frameOut, 0, led] = ringColors[2, 0, ringIdx];
                            frames[frameOut, 1, led] = ringColors[2, 1, ringIdx];
                            frames[frameOut, 2, led] = ringColors[2, 2, ringIdx];
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
                            frames[frameOut, 0, led] = ringColors[3, 0, ringIdx];
                            frames[frameOut, 1, led] = ringColors[3, 1, ringIdx];
                            frames[frameOut, 2, led] = ringColors[3, 2, ringIdx];
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
                            frames[frameOut, 0, led] = ringColors[4, 0, ringIdx];
                            frames[frameOut, 1, led] = ringColors[4, 1, ringIdx];
                            frames[frameOut, 2, led] = ringColors[4, 2, ringIdx];
                            ringIdx = ((attach != 1) ? (ringIdx - 1) : (ringIdx + 1));
                        }
                    }
                }
                frame++;
                head++;
                innerHead++;
            }
        }
        return frame;
    }
    internal int InfDrumming(int zone, int fans, int bright, int direction, int attach)
    {
        byte[,,] ringColors = new byte[5, 3, 40];
        byte[,] colorRowTable = new byte[12, 4]
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
        byte[,] brightnessTable = new byte[108, 4]
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
        int frame = 0;
        for (int step = 0; step < 108; step++)
        {
            byte columnOffset = 0;
            for (int column = 0; column < 4; column++)
            {
                byte paletteRow = colorRowTable[step / 9, column];
                byte level = brightnessTable[step, column];
                byte red = (byte)(palette[3, paletteRow, 0] * level >> 8);
                byte green = (byte)(palette[3, paletteRow, 1] * level >> 8);
                byte blue = (byte)(palette[3, paletteRow, 2] * level >> 8);
                int ringBase = (column - columnOffset) * 8;
                for (int led = 0; led < 8; led++)
                {
                    ringColors[0, 0, ringBase + led] = (byte)(red * bright >> 8);
                    ringColors[0, 1, ringBase + led] = (byte)(green * bright >> 8);
                    ringColors[0, 2, ringBase + led] = (byte)(blue * bright >> 8);
                }
                ringBase = (column - columnOffset) * 10;
                for (int led = 0; led < 10; led++)
                {
                    ringColors[1, 0, ringBase + led] = (byte)(red * bright >> 8);
                    ringColors[1, 1, ringBase + led] = (byte)(green * bright >> 8);
                    ringColors[1, 2, ringBase + led] = (byte)(blue * bright >> 8);
                    ringColors[3, 0, ringBase + led] = ringColors[1, 0, ringBase + led];
                    ringColors[3, 1, ringBase + led] = ringColors[1, 1, ringBase + led];
                    ringColors[3, 2, ringBase + led] = ringColors[1, 2, ringBase + led];
                }
                ringBase = (column - columnOffset) * 8;
                level = brightnessTable[step % 9, 0];
                red = (byte)(palette[3, paletteRow, 0] * level >> 8);
                green = (byte)(palette[3, paletteRow, 1] * level >> 8);
                blue = (byte)(palette[3, paletteRow, 2] * level >> 8);
                for (int led = 0; led < 8; led++)
                {
                    ringColors[2, 0, ringBase + led] = (byte)(red * bright >> 8);
                    ringColors[2, 1, ringBase + led] = (byte)(green * bright >> 8);
                    ringColors[2, 2, ringBase + led] = (byte)(blue * bright >> 8);
                    ringColors[4, 0, ringBase + led] = ringColors[2, 0, ringBase + led];
                    ringColors[4, 1, ringBase + led] = ringColors[2, 1, ringBase + led];
                    ringColors[4, 2, ringBase + led] = ringColors[2, 2, ringBase + led];
                }
                if (fans == 3 && column == 1)
                {
                    column = 2;
                    columnOffset = 1;
                }
                if (fans == 2 && column == 1)
                {
                    column = 3;
                    columnOffset = 2;
                }
                if (fans == 1 && column == 0)
                {
                    column = 3;
                    columnOffset = 3;
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
                        frames[frameOut, 0, led] = ringColors[0, 0, ringIdx];
                        frames[frameOut, 1, led] = ringColors[0, 1, ringIdx];
                        frames[frameOut, 2, led] = ringColors[0, 2, ringIdx];
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
                        frames[frameOut, 0, led] = ringColors[1, 0, ringIdx];
                        frames[frameOut, 1, led] = ringColors[1, 1, ringIdx];
                        frames[frameOut, 2, led] = ringColors[1, 2, ringIdx];
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
                        frames[frameOut, 0, led] = ringColors[2, 0, ringIdx];
                        frames[frameOut, 1, led] = ringColors[2, 1, ringIdx];
                        frames[frameOut, 2, led] = ringColors[2, 2, ringIdx];
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
                        frames[frameOut, 0, led] = ringColors[3, 0, ringIdx];
                        frames[frameOut, 1, led] = ringColors[3, 1, ringIdx];
                        frames[frameOut, 2, led] = ringColors[3, 2, ringIdx];
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
                        frames[frameOut, 0, led] = ringColors[4, 0, ringIdx];
                        frames[frameOut, 1, led] = ringColors[4, 1, ringIdx];
                        frames[frameOut, 2, led] = ringColors[4, 2, ringIdx];
                        ringIdx = ((attach != 1) ? (ringIdx - 1) : (ringIdx + 1));
                    }
                }
            }
            frame++;
        }
        return frame;
    }
}
