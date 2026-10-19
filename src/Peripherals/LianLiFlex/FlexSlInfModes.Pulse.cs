using System;

namespace Nexus.Service.Peripherals.LianLiFlex;

internal sealed partial class FlexSlInfModes
{
    internal int InfDoor(int zone, int fans, int bright, int direction, int attach)
    {
        byte[,,] ringColors = new byte[5, 3, 40];
        byte[,] zoneLedCountByFans = new byte[3, 4]
        {
            { 8, 16, 24, 32 },
            { 10, 20, 30, 40 },
            { 8, 16, 24, 32 }
        };
        byte[,] doorMask1 = new byte[8, 8]
        {
            { 0, 0, 1, 0, 0, 0, 1, 0 },
            { 0, 0, 1, 1, 0, 0, 1, 1 },
            { 0, 1, 1, 1, 0, 1, 1, 1 },
            { 1, 1, 1, 1, 1, 1, 1, 1 },
            { 0, 1, 1, 1, 0, 1, 1, 1 },
            { 0, 0, 1, 1, 0, 0, 1, 1 },
            { 0, 0, 1, 0, 0, 0, 1, 0 },
            { 0, 0, 0, 0, 0, 0, 0, 0 }
        };
        byte[,] doorMask2 = new byte[16, 16]
        {
            {
                0, 0, 1, 0, 0, 0, 0, 0, 0, 0,
                0, 0, 0, 0, 1, 0
            },
            {
                0, 0, 1, 1, 0, 0, 0, 0, 0, 0,
                0, 0, 0, 0, 1, 1
            },
            {
                0, 1, 1, 1, 0, 0, 0, 0, 0, 0,
                0, 0, 0, 1, 1, 1
            },
            {
                0, 1, 1, 1, 1, 0, 0, 0, 1, 0,
                0, 0, 0, 1, 1, 1
            },
            {
                1, 1, 1, 1, 1, 0, 0, 0, 1, 0,
                0, 0, 1, 1, 1, 1
            },
            {
                1, 1, 1, 1, 1, 1, 0, 0, 1, 1,
                0, 0, 1, 1, 1, 1
            },
            {
                1, 1, 1, 1, 1, 1, 0, 1, 1, 1,
                0, 1, 1, 1, 1, 1
            },
            {
                1, 1, 1, 1, 1, 1, 1, 1, 1, 1,
                1, 1, 1, 1, 1, 1
            },
            {
                1, 1, 1, 1, 1, 1, 1, 1, 1, 1,
                1, 1, 1, 1, 1, 1
            },
            {
                1, 1, 1, 1, 1, 1, 0, 1, 1, 1,
                0, 1, 1, 1, 1, 1
            },
            {
                1, 1, 1, 1, 1, 1, 0, 0, 1, 1,
                0, 0, 1, 1, 1, 1
            },
            {
                1, 1, 1, 1, 1, 0, 0, 0, 1, 0,
                0, 0, 1, 1, 1, 1
            },
            {
                0, 1, 1, 1, 1, 0, 0, 0, 1, 0,
                0, 0, 0, 1, 1, 1
            },
            {
                0, 1, 1, 1, 0, 0, 0, 0, 0, 0,
                0, 0, 0, 1, 1, 1
            },
            {
                0, 0, 1, 1, 0, 0, 0, 0, 0, 0,
                0, 0, 0, 0, 1, 1
            },
            {
                0, 0, 0, 0, 0, 0, 0, 0, 0, 0,
                0, 0, 0, 0, 0, 0
            }
        };
        byte[,] doorMask2RightAttach = new byte[16, 16]
        {
            {
                0, 0, 0, 0, 0, 0, 1, 0, 0, 0,
                1, 0, 0, 0, 0, 0
            },
            {
                0, 0, 0, 0, 0, 0, 1, 1, 0, 0,
                1, 1, 0, 0, 0, 0
            },
            {
                0, 0, 0, 0, 0, 1, 1, 1, 0, 1,
                1, 1, 0, 0, 0, 0
            },
            {
                1, 0, 0, 0, 0, 1, 1, 1, 0, 1,
                1, 1, 1, 0, 0, 0
            },
            {
                1, 0, 0, 0, 1, 1, 1, 1, 1, 1,
                1, 1, 1, 0, 0, 0
            },
            {
                1, 1, 0, 0, 1, 1, 1, 1, 1, 1,
                1, 1, 1, 1, 0, 0
            },
            {
                1, 1, 0, 1, 1, 1, 1, 1, 1, 1,
                1, 1, 1, 1, 0, 1
            },
            {
                1, 1, 1, 1, 1, 1, 1, 1, 1, 1,
                1, 1, 1, 1, 1, 1
            },
            {
                1, 1, 1, 1, 1, 1, 1, 1, 1, 1,
                1, 1, 1, 1, 1, 1
            },
            {
                1, 1, 0, 1, 1, 1, 1, 1, 1, 1,
                1, 1, 1, 1, 0, 1
            },
            {
                1, 1, 0, 0, 1, 1, 1, 1, 1, 1,
                1, 1, 1, 1, 0, 0
            },
            {
                1, 0, 0, 0, 1, 1, 1, 1, 1, 1,
                1, 1, 1, 0, 0, 0
            },
            {
                1, 0, 0, 0, 0, 1, 1, 1, 0, 1,
                1, 1, 1, 0, 0, 0
            },
            {
                0, 0, 0, 0, 0, 1, 1, 1, 0, 1,
                1, 1, 0, 0, 0, 0
            },
            {
                0, 0, 0, 0, 0, 0, 1, 1, 0, 0,
                1, 1, 0, 0, 0, 0
            },
            {
                0, 0, 0, 0, 0, 0, 0, 0, 0, 0,
                0, 0, 0, 0, 0, 0
            }
        };
        byte[,] doorMask3 = new byte[24, 24]
        {
            {
                0, 0, 1, 0, 0, 0, 0, 0, 0, 0,
                0, 0, 0, 0, 0, 0, 0, 0, 0, 0,
                0, 0, 1, 0
            },
            {
                0, 0, 1, 1, 0, 0, 0, 0, 0, 0,
                0, 0, 0, 0, 0, 0, 0, 0, 0, 0,
                0, 0, 1, 1
            },
            {
                0, 1, 1, 1, 0, 0, 0, 0, 0, 0,
                0, 0, 0, 0, 0, 0, 0, 0, 0, 0,
                0, 1, 1, 1
            },
            {
                0, 1, 1, 1, 1, 0, 0, 0, 0, 0,
                0, 0, 0, 0, 0, 0, 1, 0, 0, 0,
                0, 1, 1, 1
            },
            {
                1, 1, 1, 1, 1, 0, 0, 0, 0, 0,
                0, 0, 0, 0, 0, 0, 1, 0, 0, 0,
                1, 1, 1, 1
            },
            {
                1, 1, 1, 1, 1, 1, 0, 0, 0, 0,
                0, 0, 0, 0, 0, 0, 1, 1, 0, 0,
                1, 1, 1, 1
            },
            {
                1, 1, 1, 1, 1, 1, 0, 1, 0, 0,
                0, 0, 0, 0, 0, 0, 1, 1, 0, 1,
                1, 1, 1, 1
            },
            {
                1, 1, 1, 1, 1, 1, 1, 1, 0, 0,
                0, 0, 0, 0, 0, 0, 1, 1, 1, 1,
                1, 1, 1, 1
            },
            {
                1, 1, 1, 1, 1, 1, 1, 1, 0, 0,
                1, 0, 0, 0, 1, 0, 1, 1, 1, 1,
                1, 1, 1, 1
            },
            {
                1, 1, 1, 1, 1, 1, 1, 1, 0, 0,
                1, 1, 0, 0, 1, 1, 1, 1, 1, 1,
                1, 1, 1, 1
            },
            {
                1, 1, 1, 1, 1, 1, 1, 1, 0, 1,
                1, 1, 0, 1, 1, 1, 1, 1, 1, 1,
                1, 1, 1, 1
            },
            {
                1, 1, 1, 1, 1, 1, 1, 1, 1, 1,
                1, 1, 1, 1, 1, 1, 1, 1, 1, 1,
                1, 1, 1, 1
            },
            {
                1, 1, 1, 1, 1, 1, 1, 1, 1, 1,
                1, 1, 1, 1, 1, 1, 1, 1, 1, 1,
                1, 1, 1, 1
            },
            {
                1, 1, 1, 1, 1, 1, 1, 1, 0, 1,
                1, 1, 0, 1, 1, 1, 1, 1, 1, 1,
                1, 1, 1, 1
            },
            {
                1, 1, 1, 1, 1, 1, 1, 1, 0, 0,
                1, 1, 0, 0, 1, 1, 1, 1, 1, 1,
                1, 1, 1, 1
            },
            {
                1, 1, 1, 1, 1, 1, 1, 1, 0, 0,
                1, 0, 0, 0, 1, 0, 1, 1, 1, 1,
                1, 1, 1, 1
            },
            {
                1, 1, 1, 1, 1, 1, 1, 1, 0, 0,
                0, 0, 0, 0, 0, 0, 1, 1, 1, 1,
                1, 1, 1, 1
            },
            {
                1, 1, 1, 1, 1, 1, 0, 1, 0, 0,
                0, 0, 0, 0, 0, 0, 1, 1, 0, 1,
                1, 1, 1, 1
            },
            {
                1, 1, 1, 1, 1, 1, 0, 0, 0, 0,
                0, 0, 0, 0, 0, 0, 1, 1, 0, 0,
                1, 1, 1, 1
            },
            {
                1, 1, 1, 1, 1, 0, 0, 0, 0, 0,
                0, 0, 0, 0, 0, 0, 1, 0, 0, 0,
                1, 1, 1, 1
            },
            {
                0, 1, 1, 1, 1, 0, 0, 0, 0, 0,
                0, 0, 0, 0, 0, 0, 1, 0, 0, 0,
                0, 1, 1, 1
            },
            {
                0, 1, 1, 1, 0, 0, 0, 0, 0, 0,
                0, 0, 0, 0, 0, 0, 0, 0, 0, 0,
                0, 1, 1, 1
            },
            {
                0, 0, 1, 1, 0, 0, 0, 0, 0, 0,
                0, 0, 0, 0, 0, 0, 0, 0, 0, 0,
                0, 0, 1, 1
            },
            {
                0, 0, 0, 0, 0, 0, 0, 0, 0, 0,
                0, 0, 0, 0, 0, 0, 0, 0, 0, 0,
                0, 0, 0, 0
            }
        };
        byte[,] doorMask3RightAttach = new byte[24, 24]
        {
            {
                0, 0, 0, 0, 0, 0, 1, 0, 0, 0,
                0, 0, 0, 0, 0, 0, 0, 0, 1, 0,
                0, 0, 0, 0
            },
            {
                0, 0, 0, 0, 0, 0, 1, 1, 0, 0,
                0, 0, 0, 0, 0, 0, 0, 0, 1, 1,
                0, 0, 0, 0
            },
            {
                0, 0, 0, 0, 0, 1, 1, 1, 0, 0,
                0, 0, 0, 0, 0, 0, 0, 1, 1, 1,
                0, 0, 0, 0
            },
            {
                1, 0, 0, 0, 0, 1, 1, 1, 0, 0,
                0, 0, 0, 0, 0, 0, 0, 1, 1, 1,
                1, 0, 0, 0
            },
            {
                1, 0, 0, 0, 1, 1, 1, 1, 0, 0,
                0, 0, 0, 0, 0, 0, 1, 1, 1, 1,
                1, 0, 0, 0
            },
            {
                1, 1, 0, 0, 1, 1, 1, 1, 0, 0,
                0, 0, 0, 0, 0, 0, 1, 1, 1, 1,
                1, 1, 0, 0
            },
            {
                1, 1, 0, 1, 1, 1, 1, 1, 0, 0,
                0, 0, 0, 0, 0, 0, 1, 1, 1, 1,
                1, 1, 0, 1
            },
            {
                1, 1, 1, 1, 1, 1, 1, 1, 0, 0,
                0, 0, 0, 0, 0, 0, 1, 1, 1, 1,
                1, 1, 1, 1
            },
            {
                1, 1, 1, 1, 1, 1, 1, 1, 0, 0,
                1, 0, 0, 0, 1, 0, 1, 1, 1, 1,
                1, 1, 1, 1
            },
            {
                1, 1, 1, 1, 1, 1, 1, 1, 0, 0,
                1, 1, 0, 0, 1, 1, 1, 1, 1, 1,
                1, 1, 1, 1
            },
            {
                1, 1, 1, 1, 1, 1, 1, 1, 0, 1,
                1, 1, 0, 1, 1, 1, 1, 1, 1, 1,
                1, 1, 1, 1
            },
            {
                1, 1, 1, 1, 1, 1, 1, 1, 1, 1,
                1, 1, 1, 1, 1, 1, 1, 1, 1, 1,
                1, 1, 1, 1
            },
            {
                1, 1, 1, 1, 1, 1, 1, 1, 1, 1,
                1, 1, 1, 1, 1, 1, 1, 1, 1, 1,
                1, 1, 1, 1
            },
            {
                1, 1, 1, 1, 1, 1, 1, 1, 0, 1,
                1, 1, 0, 1, 1, 1, 1, 1, 1, 1,
                1, 1, 1, 1
            },
            {
                1, 1, 1, 1, 1, 1, 1, 1, 0, 0,
                1, 1, 0, 0, 1, 1, 1, 1, 1, 1,
                1, 1, 1, 1
            },
            {
                1, 1, 1, 1, 1, 1, 1, 1, 0, 0,
                1, 0, 0, 0, 1, 0, 1, 1, 1, 1,
                1, 1, 1, 1
            },
            {
                1, 1, 1, 1, 1, 1, 1, 1, 0, 0,
                0, 0, 0, 0, 0, 0, 1, 1, 1, 1,
                1, 1, 1, 1
            },
            {
                1, 1, 0, 1, 1, 1, 1, 1, 0, 0,
                0, 0, 0, 0, 0, 0, 1, 1, 1, 1,
                1, 1, 0, 1
            },
            {
                1, 1, 0, 0, 1, 1, 1, 1, 0, 0,
                0, 0, 0, 0, 0, 0, 1, 1, 1, 1,
                1, 1, 0, 0
            },
            {
                1, 0, 0, 0, 1, 1, 1, 1, 0, 0,
                0, 0, 0, 0, 0, 0, 1, 1, 1, 1,
                1, 0, 0, 0
            },
            {
                1, 0, 0, 0, 0, 1, 1, 1, 0, 0,
                0, 0, 0, 0, 0, 0, 0, 1, 1, 1,
                1, 0, 0, 0
            },
            {
                0, 0, 0, 0, 0, 1, 1, 1, 0, 0,
                0, 0, 0, 0, 0, 0, 0, 1, 1, 1,
                0, 0, 0, 0
            },
            {
                0, 0, 0, 0, 0, 0, 1, 1, 0, 0,
                0, 0, 0, 0, 0, 0, 0, 0, 1, 1,
                0, 0, 0, 0
            },
            {
                0, 0, 0, 0, 0, 0, 0, 0, 0, 0,
                0, 0, 0, 0, 0, 0, 0, 0, 0, 0,
                0, 0, 0, 0
            }
        };
        byte[,] doorMask4 = new byte[32, 32]
        {
            {
                0, 0, 1, 0, 0, 0, 0, 0, 0, 0,
                0, 0, 0, 0, 0, 0, 0, 0, 0, 0,
                0, 0, 0, 0, 0, 0, 0, 0, 0, 0,
                1, 0
            },
            {
                0, 0, 1, 1, 0, 0, 0, 0, 0, 0,
                0, 0, 0, 0, 0, 0, 0, 0, 0, 0,
                0, 0, 0, 0, 0, 0, 0, 0, 0, 0,
                1, 1
            },
            {
                0, 1, 1, 1, 0, 0, 0, 0, 0, 0,
                0, 0, 0, 0, 0, 0, 0, 0, 0, 0,
                0, 0, 0, 0, 0, 0, 0, 0, 0, 1,
                1, 1
            },
            {
                0, 1, 1, 1, 1, 0, 0, 0, 0, 0,
                0, 0, 0, 0, 0, 0, 0, 0, 0, 0,
                0, 0, 0, 0, 1, 0, 0, 0, 0, 1,
                1, 1
            },
            {
                1, 1, 1, 1, 1, 0, 0, 0, 0, 0,
                0, 0, 0, 0, 0, 0, 0, 0, 0, 0,
                0, 0, 0, 0, 1, 0, 0, 0, 1, 1,
                1, 1
            },
            {
                1, 1, 1, 1, 1, 1, 0, 0, 0, 0,
                0, 0, 0, 0, 0, 0, 0, 0, 0, 0,
                0, 0, 0, 0, 1, 1, 0, 0, 1, 1,
                1, 1
            },
            {
                1, 1, 1, 1, 1, 1, 0, 1, 0, 0,
                0, 0, 0, 0, 0, 0, 0, 0, 0, 0,
                0, 0, 0, 0, 1, 1, 0, 1, 1, 1,
                1, 1
            },
            {
                1, 1, 1, 1, 1, 1, 1, 1, 0, 0,
                0, 0, 0, 0, 0, 0, 0, 0, 0, 0,
                0, 0, 0, 0, 1, 1, 1, 1, 1, 1,
                1, 1
            },
            {
                1, 1, 1, 1, 1, 1, 1, 1, 0, 0,
                1, 0, 0, 0, 0, 0, 0, 0, 0, 0,
                0, 0, 1, 0, 1, 1, 1, 1, 1, 1,
                1, 1
            },
            {
                1, 1, 1, 1, 1, 1, 1, 1, 0, 0,
                1, 1, 0, 0, 0, 0, 0, 0, 0, 0,
                0, 0, 1, 1, 1, 1, 1, 1, 1, 1,
                1, 1
            },
            {
                1, 1, 1, 1, 1, 1, 1, 1, 0, 1,
                1, 1, 0, 0, 0, 0, 0, 0, 0, 0,
                0, 1, 1, 1, 1, 1, 1, 1, 1, 1,
                1, 1
            },
            {
                1, 1, 1, 1, 1, 1, 1, 1, 0, 1,
                1, 1, 1, 0, 0, 0, 1, 0, 0, 0,
                0, 1, 1, 1, 1, 1, 1, 1, 1, 1,
                1, 1
            },
            {
                1, 1, 1, 1, 1, 1, 1, 1, 1, 1,
                1, 1, 1, 0, 0, 0, 1, 0, 0, 0,
                1, 1, 1, 1, 1, 1, 1, 1, 1, 1,
                1, 1
            },
            {
                1, 1, 1, 1, 1, 1, 1, 1, 1, 1,
                1, 1, 1, 1, 0, 0, 1, 1, 0, 0,
                1, 1, 1, 1, 1, 1, 1, 1, 1, 1,
                1, 1
            },
            {
                1, 1, 1, 1, 1, 1, 1, 1, 1, 1,
                1, 1, 1, 1, 0, 1, 1, 1, 0, 1,
                1, 1, 1, 1, 1, 1, 1, 1, 1, 1,
                1, 1
            },
            {
                1, 1, 1, 1, 1, 1, 1, 1, 1, 1,
                1, 1, 1, 1, 1, 1, 1, 1, 1, 1,
                1, 1, 1, 1, 1, 1, 1, 1, 1, 1,
                1, 1
            },
            {
                1, 1, 1, 1, 1, 1, 1, 1, 1, 1,
                1, 1, 1, 1, 1, 1, 1, 1, 1, 1,
                1, 1, 1, 1, 1, 1, 1, 1, 1, 1,
                1, 1
            },
            {
                1, 1, 1, 1, 1, 1, 1, 1, 1, 1,
                1, 1, 1, 1, 0, 1, 1, 1, 0, 1,
                1, 1, 1, 1, 1, 1, 1, 1, 1, 1,
                1, 1
            },
            {
                1, 1, 1, 1, 1, 1, 1, 1, 1, 1,
                1, 1, 1, 1, 0, 0, 1, 1, 0, 0,
                1, 1, 1, 1, 1, 1, 1, 1, 1, 1,
                1, 1
            },
            {
                1, 1, 1, 1, 1, 1, 1, 1, 1, 1,
                1, 1, 1, 0, 0, 0, 1, 0, 0, 0,
                1, 1, 1, 1, 1, 1, 1, 1, 1, 1,
                1, 1
            },
            {
                1, 1, 1, 1, 1, 1, 1, 1, 0, 1,
                1, 1, 1, 0, 0, 0, 1, 0, 0, 0,
                0, 1, 1, 1, 1, 1, 1, 1, 1, 1,
                1, 1
            },
            {
                1, 1, 1, 1, 1, 1, 1, 1, 0, 1,
                1, 1, 0, 0, 0, 0, 0, 0, 0, 0,
                0, 1, 1, 1, 1, 1, 1, 1, 1, 1,
                1, 1
            },
            {
                1, 1, 1, 1, 1, 1, 1, 1, 0, 0,
                1, 1, 0, 0, 0, 0, 0, 0, 0, 0,
                0, 0, 1, 1, 1, 1, 1, 1, 1, 1,
                1, 1
            },
            {
                1, 1, 1, 1, 1, 1, 1, 1, 0, 0,
                1, 0, 0, 0, 0, 0, 0, 0, 0, 0,
                0, 0, 1, 0, 1, 1, 1, 1, 1, 1,
                1, 1
            },
            {
                1, 1, 1, 1, 1, 1, 1, 1, 0, 0,
                0, 0, 0, 0, 0, 0, 0, 0, 0, 0,
                0, 0, 0, 0, 1, 1, 1, 1, 1, 1,
                1, 1
            },
            {
                1, 1, 1, 1, 1, 1, 0, 1, 0, 0,
                0, 0, 0, 0, 0, 0, 0, 0, 0, 0,
                0, 0, 0, 0, 1, 1, 0, 1, 1, 1,
                1, 1
            },
            {
                1, 1, 1, 1, 1, 1, 0, 0, 0, 0,
                0, 0, 0, 0, 0, 0, 0, 0, 0, 0,
                0, 0, 0, 0, 1, 1, 0, 0, 1, 1,
                1, 1
            },
            {
                1, 1, 1, 1, 1, 0, 0, 0, 0, 0,
                0, 0, 0, 0, 0, 0, 0, 0, 0, 0,
                0, 0, 0, 0, 1, 0, 0, 0, 1, 1,
                1, 1
            },
            {
                0, 1, 1, 1, 1, 0, 0, 0, 0, 0,
                0, 0, 0, 0, 0, 0, 0, 0, 0, 0,
                0, 0, 0, 0, 1, 0, 0, 0, 0, 1,
                1, 1
            },
            {
                0, 1, 1, 1, 0, 0, 0, 0, 0, 0,
                0, 0, 0, 0, 0, 0, 0, 0, 0, 0,
                0, 0, 0, 0, 0, 0, 0, 0, 0, 1,
                1, 1
            },
            {
                0, 0, 1, 1, 0, 0, 0, 0, 0, 0,
                0, 0, 0, 0, 0, 0, 0, 0, 0, 0,
                0, 0, 0, 0, 0, 0, 0, 0, 0, 0,
                1, 1
            },
            {
                0, 0, 0, 0, 0, 0, 0, 0, 0, 0,
                0, 0, 0, 0, 0, 0, 0, 0, 0, 0,
                0, 0, 0, 0, 0, 0, 0, 0, 0, 0,
                0, 0
            }
        };
        byte[,] doorMask4RightAttach = new byte[32, 32]
        {
            {
                0, 0, 0, 0, 0, 0, 1, 0, 0, 0,
                0, 0, 0, 0, 0, 0, 0, 0, 0, 0,
                0, 0, 0, 0, 0, 0, 1, 0, 0, 0,
                0, 0
            },
            {
                0, 0, 0, 0, 0, 0, 1, 1, 0, 0,
                0, 0, 0, 0, 0, 0, 0, 0, 0, 0,
                0, 0, 0, 0, 0, 0, 1, 1, 0, 0,
                0, 0
            },
            {
                0, 0, 0, 0, 0, 1, 1, 1, 0, 0,
                0, 0, 0, 0, 0, 0, 0, 0, 0, 0,
                0, 0, 0, 0, 0, 1, 1, 1, 0, 0,
                0, 0
            },
            {
                1, 0, 0, 0, 0, 1, 1, 1, 0, 0,
                0, 0, 0, 0, 0, 0, 0, 0, 0, 0,
                0, 0, 0, 0, 0, 1, 1, 1, 1, 0,
                0, 0
            },
            {
                1, 0, 0, 0, 1, 1, 1, 1, 0, 0,
                0, 0, 0, 0, 0, 0, 0, 0, 0, 0,
                0, 0, 0, 0, 1, 1, 1, 1, 1, 0,
                0, 0
            },
            {
                1, 1, 0, 0, 1, 1, 1, 1, 0, 0,
                0, 0, 0, 0, 0, 0, 0, 0, 0, 0,
                0, 0, 0, 0, 1, 1, 1, 1, 1, 1,
                0, 0
            },
            {
                1, 1, 0, 1, 1, 1, 1, 1, 0, 0,
                0, 0, 0, 0, 0, 0, 0, 0, 0, 0,
                0, 0, 0, 0, 1, 1, 1, 1, 1, 1,
                0, 1
            },
            {
                1, 1, 1, 1, 1, 1, 1, 1, 0, 0,
                0, 0, 0, 0, 0, 0, 0, 0, 0, 0,
                0, 0, 0, 0, 1, 1, 1, 1, 1, 1,
                1, 1
            },
            {
                1, 1, 1, 1, 1, 1, 1, 1, 0, 0,
                0, 0, 0, 0, 1, 0, 0, 0, 1, 0,
                0, 0, 0, 0, 1, 1, 1, 1, 1, 1,
                1, 1
            },
            {
                1, 1, 1, 1, 1, 1, 1, 1, 0, 0,
                0, 0, 0, 0, 1, 1, 0, 0, 1, 1,
                0, 0, 0, 0, 1, 1, 1, 1, 1, 1,
                1, 1
            },
            {
                1, 1, 1, 1, 1, 1, 1, 1, 0, 0,
                0, 0, 0, 1, 1, 1, 0, 1, 1, 1,
                0, 0, 0, 0, 1, 1, 1, 1, 1, 1,
                1, 1
            },
            {
                1, 1, 1, 1, 1, 1, 1, 1, 1, 0,
                0, 0, 0, 1, 1, 1, 0, 1, 1, 1,
                1, 0, 0, 0, 1, 1, 1, 1, 1, 1,
                1, 1
            },
            {
                1, 1, 1, 1, 1, 1, 1, 1, 1, 0,
                0, 0, 1, 1, 1, 1, 1, 1, 1, 1,
                1, 0, 0, 0, 1, 1, 1, 1, 1, 1,
                1, 1
            },
            {
                1, 1, 1, 1, 1, 1, 1, 1, 1, 1,
                0, 0, 1, 1, 1, 1, 1, 1, 1, 1,
                1, 1, 0, 0, 1, 1, 1, 1, 1, 1,
                1, 1
            },
            {
                1, 1, 1, 1, 1, 1, 1, 1, 1, 1,
                0, 1, 1, 1, 1, 1, 1, 1, 1, 1,
                1, 1, 0, 1, 1, 1, 1, 1, 1, 1,
                1, 1
            },
            {
                1, 1, 1, 1, 1, 1, 1, 1, 1, 1,
                1, 1, 1, 1, 1, 1, 1, 1, 1, 1,
                1, 1, 1, 1, 1, 1, 1, 1, 1, 1,
                1, 1
            },
            {
                1, 1, 1, 1, 1, 1, 1, 1, 1, 1,
                1, 1, 1, 1, 1, 1, 1, 1, 1, 1,
                1, 1, 1, 1, 1, 1, 1, 1, 1, 1,
                1, 1
            },
            {
                1, 1, 1, 1, 1, 1, 1, 1, 1, 1,
                0, 1, 1, 1, 1, 1, 1, 1, 1, 1,
                1, 1, 0, 1, 1, 1, 1, 1, 1, 1,
                1, 1
            },
            {
                1, 1, 1, 1, 1, 1, 1, 1, 1, 1,
                0, 0, 1, 1, 1, 1, 1, 1, 1, 1,
                1, 1, 0, 0, 1, 1, 1, 1, 1, 1,
                1, 1
            },
            {
                1, 1, 1, 1, 1, 1, 1, 1, 1, 0,
                0, 0, 1, 1, 1, 1, 1, 1, 1, 1,
                1, 0, 0, 0, 1, 1, 1, 1, 1, 1,
                1, 1
            },
            {
                1, 1, 1, 1, 1, 1, 1, 1, 1, 0,
                0, 0, 0, 1, 1, 1, 0, 1, 1, 1,
                1, 0, 0, 0, 1, 1, 1, 1, 1, 1,
                1, 1
            },
            {
                1, 1, 1, 1, 1, 1, 1, 1, 0, 0,
                0, 0, 0, 1, 1, 1, 0, 1, 1, 1,
                0, 0, 0, 0, 1, 1, 1, 1, 1, 1,
                1, 1
            },
            {
                1, 1, 1, 1, 1, 1, 1, 1, 0, 0,
                0, 0, 0, 0, 1, 1, 0, 0, 1, 1,
                0, 0, 0, 0, 1, 1, 1, 1, 1, 1,
                1, 1
            },
            {
                1, 1, 1, 1, 1, 1, 1, 1, 0, 0,
                0, 0, 0, 0, 1, 0, 0, 0, 1, 0,
                0, 0, 0, 0, 1, 1, 1, 1, 1, 1,
                1, 1
            },
            {
                1, 1, 1, 1, 1, 1, 1, 1, 0, 0,
                0, 0, 0, 0, 0, 0, 0, 0, 0, 0,
                0, 0, 0, 0, 1, 1, 1, 1, 1, 1,
                1, 1
            },
            {
                1, 1, 0, 1, 1, 1, 1, 1, 0, 0,
                0, 0, 0, 0, 0, 0, 0, 0, 0, 0,
                0, 0, 0, 0, 1, 1, 1, 1, 1, 1,
                0, 1
            },
            {
                1, 1, 0, 0, 1, 1, 1, 1, 0, 0,
                0, 0, 0, 0, 0, 0, 0, 0, 0, 0,
                0, 0, 0, 0, 1, 1, 1, 1, 1, 1,
                0, 0
            },
            {
                1, 0, 0, 0, 1, 1, 1, 1, 0, 0,
                0, 0, 0, 0, 0, 0, 0, 0, 0, 0,
                0, 0, 0, 0, 1, 1, 1, 1, 1, 0,
                0, 0
            },
            {
                1, 0, 0, 0, 0, 1, 1, 1, 0, 0,
                0, 0, 0, 0, 0, 0, 0, 0, 0, 0,
                0, 0, 0, 0, 0, 1, 1, 1, 1, 0,
                0, 0
            },
            {
                0, 0, 0, 0, 0, 1, 1, 1, 0, 0,
                0, 0, 0, 0, 0, 0, 0, 0, 0, 0,
                0, 0, 0, 0, 0, 1, 1, 1, 0, 0,
                0, 0
            },
            {
                0, 0, 0, 0, 0, 0, 1, 1, 0, 0,
                0, 0, 0, 0, 0, 0, 0, 0, 0, 0,
                0, 0, 0, 0, 0, 0, 1, 1, 0, 0,
                0, 0
            },
            {
                0, 0, 0, 0, 0, 0, 0, 0, 0, 0,
                0, 0, 0, 0, 0, 0, 0, 0, 0, 0,
                0, 0, 0, 0, 0, 0, 0, 0, 0, 0,
                0, 0
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
        byte centerOuterHalf = (byte)(zoneLedCountByFans[0, fans - 1] / 2);
        byte innerHalf = (byte)(zoneLedCountByFans[1, fans - 1] / 2);
        int frame = 0;
        for (byte colorIdx = 0; colorIdx < 4; colorIdx++)
        {
            for (int pass = 0; pass < 2; pass++)
            {
                int innerHead = 0;
                int head = 0;
                while (head < centerOuterHalf)
                {
                    for (int led = 0; led < centerOuterHalf * 2; led++)
                    {
                        int maskRow = pass * centerOuterHalf + head;
                        byte maskValue;
                        if (attach == 0)
                        {
                            maskValue = doorMask4[maskRow, led];
                            if (fans == 1)
                            {
                                maskValue = doorMask1[maskRow, led];
                            }
                            if (fans == 2)
                            {
                                maskValue = doorMask2[maskRow, led];
                            }
                            if (fans == 3)
                            {
                                maskValue = doorMask3[maskRow, led];
                            }
                        }
                        else
                        {
                            maskValue = doorMask4RightAttach[maskRow, led];
                            if (fans == 1)
                            {
                                maskValue = doorMask1[maskRow, led];
                            }
                            if (fans == 2)
                            {
                                maskValue = doorMask2RightAttach[maskRow, led];
                            }
                            if (fans == 3)
                            {
                                maskValue = doorMask3RightAttach[maskRow, led];
                            }
                        }
                        byte red = 0;
                        byte green = 0;
                        byte blue = 0;
                        if (maskValue != 0)
                        {
                            red = palette[2, colorIdx, 0];
                            green = palette[2, colorIdx, 1];
                            blue = palette[2, colorIdx, 2];
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
                        if (led < head && pass == 0)
                        {
                            red = palette[0, colorIdx, 0];
                            green = palette[0, colorIdx, 1];
                            blue = palette[0, colorIdx, 2];
                        }
                        if (led <= centerOuterHalf - head - 1 && pass == 1)
                        {
                            red = palette[0, colorIdx, 0];
                            green = palette[0, colorIdx, 1];
                            blue = palette[0, colorIdx, 2];
                        }
                        ringColors[2, 0, led] = (byte)(red * bright >> 8);
                        ringColors[2, 1, led] = (byte)(green * bright >> 8);
                        ringColors[2, 2, led] = (byte)(blue * bright >> 8);
                        ringColors[2, 0, centerOuterHalf + centerOuterHalf - led - 1] = ringColors[2, 0, led];
                        ringColors[2, 1, centerOuterHalf + centerOuterHalf - led - 1] = ringColors[2, 1, led];
                        ringColors[2, 2, centerOuterHalf + centerOuterHalf - led - 1] = ringColors[2, 2, led];
                        ringColors[4, 0, led] = ringColors[2, 0, led];
                        ringColors[4, 1, led] = ringColors[2, 1, led];
                        ringColors[4, 2, led] = ringColors[2, 2, led];
                        ringColors[4, 0, centerOuterHalf + centerOuterHalf - led - 1] = ringColors[2, 0, led];
                        ringColors[4, 1, centerOuterHalf + centerOuterHalf - led - 1] = ringColors[2, 1, led];
                        ringColors[4, 2, centerOuterHalf + centerOuterHalf - led - 1] = ringColors[2, 2, led];
                    }
                    if (head == 2 || head == 6 || head == 10 || head == 14 || head == 18 || head == 22 || head == 26 || head == 30)
                    {
                        innerHead++;
                    }
                    for (int led = 0; led < innerHalf; led++)
                    {
                        byte red = 0;
                        byte green = 0;
                        byte blue = 0;
                        if (led < innerHead && pass == 0)
                        {
                            red = palette[1, colorIdx, 0];
                            green = palette[1, colorIdx, 1];
                            blue = palette[1, colorIdx, 2];
                        }
                        if (led <= innerHalf - innerHead - 1 && pass == 1)
                        {
                            red = palette[1, colorIdx, 0];
                            green = palette[1, colorIdx, 1];
                            blue = palette[1, colorIdx, 2];
                        }
                        ringColors[1, 0, led] = (byte)(red * bright >> 8);
                        ringColors[1, 1, led] = (byte)(green * bright >> 8);
                        ringColors[1, 2, led] = (byte)(blue * bright >> 8);
                        ringColors[1, 0, innerHalf + innerHalf - led - 1] = ringColors[1, 0, led];
                        ringColors[1, 1, innerHalf + innerHalf - led - 1] = ringColors[1, 1, led];
                        ringColors[1, 2, innerHalf + innerHalf - led - 1] = ringColors[1, 2, led];
                        ringColors[3, 0, led] = ringColors[1, 0, led];
                        ringColors[3, 1, led] = ringColors[1, 1, led];
                        ringColors[3, 2, led] = ringColors[1, 2, led];
                        ringColors[3, 0, innerHalf + innerHalf - led - 1] = ringColors[1, 0, led];
                        ringColors[3, 1, innerHalf + innerHalf - led - 1] = ringColors[1, 1, led];
                        ringColors[3, 2, innerHalf + innerHalf - led - 1] = ringColors[1, 2, led];
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
        }
        return frame;
    }
    internal int InfHeartBeat(int zone, int fans, int bright, int direction, int attach)
    {
        byte[,,] ringColors = new byte[5, 3, 176];
        byte[,] zoneLedCountByFans = new byte[3, 4]
        {
            { 8, 16, 24, 32 },
            { 10, 20, 30, 40 },
            { 8, 16, 24, 32 }
        };
        byte[] pulseTable = new byte[96]
        {
            16, 16, 16, 16, 16, 16, 16, 16, 16, 16,
            16, 16, 16, 16, 16, 16, 16, 16, 24, 32,
            40, 48, 56, 64, 72, 80, 88, 96, 104, 112,
            120, 128, 136, 144, 152, 160, 168, 176, 184, 192,
            200, 208, 216, 224, 232, 240, 248, 255, 248, 240,
            232, 224, 216, 208, 200, 192, 184, 176, 168, 160,
            152, 144, 136, 128, 120, 112, 104, 96, 88, 80,
            72, 64, 56, 48, 40, 32, 24, 16, 16, 16,
            16, 16, 16, 16, 16, 16, 16, 16, 16, 16,
            16, 16, 16, 16, 16, 16
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
        for (int step = 0; step < 128; step++)
        {
            for (int fan = 0; fan < fans; fan++)
            {
                int ringBase = fan * 8;
                byte pulseIdx = (byte)step;
                if (step >= 96)
                {
                    pulseIdx = 0;
                }
                for (int led = 0; led < 8; led++)
                {
                    byte red = (byte)(palette[2, 0, 0] * pulseTable[pulseIdx] >> 8);
                    byte green = (byte)(palette[2, 0, 1] * pulseTable[pulseIdx] >> 8);
                    byte blue = (byte)(palette[2, 0, 2] * pulseTable[pulseIdx] >> 8);
                    ringColors[0, 0, ringBase + led] = (byte)(red * bright >> 8);
                    ringColors[0, 1, ringBase + led] = (byte)(green * bright >> 8);
                    ringColors[0, 2, ringBase + led] = (byte)(blue * bright >> 8);
                }
                ringBase = fan * 10;
                pulseIdx = 0;
                if (step >= 32)
                {
                    pulseIdx = (byte)(step - 32);
                }
                for (int led = 0; led < 10; led++)
                {
                    byte red = (byte)(palette[1, 0, 0] * pulseTable[pulseIdx] >> 8);
                    byte green = (byte)(palette[1, 0, 1] * pulseTable[pulseIdx] >> 8);
                    byte blue = (byte)(palette[1, 0, 2] * pulseTable[pulseIdx] >> 8);
                    ringColors[1, 0, ringBase + led] = (byte)(red * bright >> 8);
                    ringColors[1, 1, ringBase + led] = (byte)(green * bright >> 8);
                    ringColors[1, 2, ringBase + led] = (byte)(blue * bright >> 8);
                    ringColors[3, 0, ringBase + led] = ringColors[1, 0, ringBase + led];
                    ringColors[3, 1, ringBase + led] = ringColors[1, 1, ringBase + led];
                    ringColors[3, 2, ringBase + led] = ringColors[1, 2, ringBase + led];
                }
                ringBase = fan * 8;
                pulseIdx = 0;
                if (step >= 32)
                {
                    pulseIdx = (byte)(step - 32);
                }
                for (int led = 0; led < 8; led++)
                {
                    byte red = (byte)(palette[0, 0, 0] * pulseTable[pulseIdx] >> 8);
                    byte green = (byte)(palette[0, 0, 1] * pulseTable[pulseIdx] >> 8);
                    byte blue = (byte)(palette[0, 0, 2] * pulseTable[pulseIdx] >> 8);
                    ringColors[2, 0, ringBase + led] = (byte)(red * bright >> 8);
                    ringColors[2, 1, ringBase + led] = (byte)(green * bright >> 8);
                    ringColors[2, 2, ringBase + led] = (byte)(blue * bright >> 8);
                    ringColors[4, 0, ringBase + led] = ringColors[2, 0, ringBase + led];
                    ringColors[4, 1, ringBase + led] = ringColors[2, 1, ringBase + led];
                    ringColors[4, 2, ringBase + led] = ringColors[2, 2, ringBase + led];
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
        int outFrames = frame / 3;
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
    internal int InfHeartBeatRunway(int zone, int fans, int bright, int direction, int attach)
    {
        byte[,,] ringColors = new byte[5, 3, 44];
        byte[] travelByFans = new byte[4] { 96, 144, 160, 168 };
        byte[,] zoneLedCountByFans = new byte[3, 4]
        {
            { 8, 16, 24, 32 },
            { 10, 20, 30, 40 },
            { 8, 16, 24, 32 }
        };
        byte[] fadeTable = new byte[96]
        {
            16, 16, 16, 16, 16, 16, 16, 16, 16, 16,
            16, 16, 16, 16, 16, 16, 16, 16, 24, 32,
            40, 48, 56, 64, 72, 80, 88, 96, 104, 112,
            120, 128, 136, 144, 152, 160, 168, 176, 184, 192,
            200, 208, 216, 224, 232, 240, 248, 255, 248, 240,
            232, 224, 216, 208, 200, 192, 184, 176, 168, 160,
            152, 144, 136, 128, 120, 112, 104, 96, 88, 80,
            72, 64, 56, 48, 40, 32, 24, 16, 16, 16,
            16, 16, 16, 16, 16, 16, 16, 16, 16, 16,
            16, 16, 16, 16, 16, 16
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
        byte ringLeds = zoneLedCountByFans[zone, 0];
        int segment = 72 / fans;
        int frame = 0;
        for (int step = 0; step < travelByFans[fans - 1] + segment; step++)
        {
            for (int ring = 0; ring < 5; ring++)
            {
                ringLeds = 8;
                if (ring == 1 || ring == 3)
                {
                    ringLeds = 10;
                }
                int ringStep = step;
                if (ring != 0)
                {
                    ringStep = ((step >= 64) ? (step - 64) : 0);
                }
                int tableIdx = ringStep;
                if (ringStep >= 96)
                {
                    tableIdx = 0;
                }
                byte red = (byte)(palette[0, 0, 0] * fadeTable[tableIdx] >> 8);
                byte green = (byte)(palette[0, 0, 1] * fadeTable[tableIdx] >> 8);
                byte blue = (byte)(palette[0, 0, 2] * fadeTable[tableIdx] >> 8);
                for (int led = 0; led < ringLeds; led++)
                {
                    int stripIdx = led;
                    if (direction == 1)
                    {
                        stripIdx = ringLeds * fans - led - 1;
                    }
                    ringColors[ring, 0, stripIdx] = (byte)(red * bright >> 8);
                    ringColors[ring, 1, stripIdx] = (byte)(green * bright >> 8);
                    ringColors[ring, 2, stripIdx] = (byte)(blue * bright >> 8);
                }
                if (fans > 1)
                {
                    tableIdx = 0;
                    if (ringStep >= segment)
                    {
                        tableIdx = ringStep - segment;
                    }
                    if (tableIdx >= 96)
                    {
                        tableIdx = 0;
                    }
                    red = (byte)(palette[0, 0, 0] * fadeTable[tableIdx] >> 8);
                    green = (byte)(palette[0, 0, 1] * fadeTable[tableIdx] >> 8);
                    blue = (byte)(palette[0, 0, 2] * fadeTable[tableIdx] >> 8);
                    for (int led = 0; led < ringLeds; led++)
                    {
                        int stripIdx = ringLeds + led;
                        if (direction == 1)
                        {
                            stripIdx = ringLeds * fans - stripIdx - 1;
                        }
                        ringColors[ring, 0, stripIdx] = (byte)(red * bright >> 8);
                        ringColors[ring, 1, stripIdx] = (byte)(green * bright >> 8);
                        ringColors[ring, 2, stripIdx] = (byte)(blue * bright >> 8);
                    }
                }
                if (fans > 2)
                {
                    tableIdx = 0;
                    if (ringStep >= segment * 2)
                    {
                        tableIdx = ringStep - segment * 2;
                    }
                    if (tableIdx >= 96)
                    {
                        tableIdx = 0;
                    }
                    red = (byte)(palette[0, 0, 0] * fadeTable[tableIdx] >> 8);
                    green = (byte)(palette[0, 0, 1] * fadeTable[tableIdx] >> 8);
                    blue = (byte)(palette[0, 0, 2] * fadeTable[tableIdx] >> 8);
                    for (int led = 0; led < ringLeds; led++)
                    {
                        int stripIdx = ringLeds + ringLeds + led;
                        if (direction == 1)
                        {
                            stripIdx = ringLeds * fans - stripIdx - 1;
                        }
                        ringColors[ring, 0, stripIdx] = (byte)(red * bright >> 8);
                        ringColors[ring, 1, stripIdx] = (byte)(green * bright >> 8);
                        ringColors[ring, 2, stripIdx] = (byte)(blue * bright >> 8);
                    }
                }
                if (fans <= 3)
                {
                    continue;
                }
                tableIdx = 0;
                if (ringStep >= segment * 3)
                {
                    tableIdx = ringStep - segment * 3;
                }
                if (tableIdx >= 96)
                {
                    tableIdx = 0;
                }
                red = (byte)(palette[0, 0, 0] * fadeTable[tableIdx] >> 8);
                green = (byte)(palette[0, 0, 1] * fadeTable[tableIdx] >> 8);
                blue = (byte)(palette[0, 0, 2] * fadeTable[tableIdx] >> 8);
                for (int led = 0; led < ringLeds; led++)
                {
                    int stripIdx = ringLeds + ringLeds + ringLeds + led;
                    if (direction == 1)
                    {
                        stripIdx = ringLeds * fans - stripIdx - 1;
                    }
                    ringColors[ring, 0, stripIdx] = (byte)(red * bright >> 8);
                    ringColors[ring, 1, stripIdx] = (byte)(green * bright >> 8);
                    ringColors[ring, 2, stripIdx] = (byte)(blue * bright >> 8);
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
        }
        return frame;
    }
    internal int InfDisco(int zone, int fans, int bright, int direction, int attach)
    {
        byte[,] strip = new byte[3, 176];
        byte[,] zoneColors = new byte[3, 10];
        byte[] halfPattern8 = new byte[8] { 0, 0, 1, 1, 2, 2, 3, 3 };
        byte[] halfPattern10 = new byte[10] { 0, 0, 1, 1, 1, 2, 2, 3, 3, 3 };
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
        for (int led = 0; led < zoneLeds; led++)
        {
            int paletteRow = halfPattern10[led];
            if (zoneLeds == 8)
            {
                paletteRow = halfPattern8[led];
            }
            zoneColors[0, led] = (byte)(palette[zone, paletteRow, 0] * bright >> 8);
            zoneColors[1, led] = (byte)(palette[zone, paletteRow, 1] * bright >> 8);
            zoneColors[2, led] = (byte)(palette[zone, paletteRow, 2] * bright >> 8);
        }
        int frame = 0;
        for (int shift = 0; shift < zoneLeds; shift++)
        {
            int colorIdx = shift;
            for (int led = 0; led < zoneLeds; led++)
            {
                int stripIdx = led;
                if (direction == 0)
                {
                    stripIdx = zoneLeds - led - 1;
                }
                strip[0, stripIdx] = zoneColors[0, colorIdx];
                strip[1, stripIdx] = zoneColors[1, colorIdx];
                strip[2, stripIdx] = zoneColors[2, colorIdx];
                colorIdx++;
                if (colorIdx >= zoneLeds)
                {
                    colorIdx -= zoneLeds;
                }
            }
            for (int fan = 0; fan < fans - 1; fan++)
            {
                for (int led = 0; led < zoneLeds; led++)
                {
                    strip[0, (fan + 1) * 8 + led] = strip[0, led];
                    strip[1, (fan + 1) * 8 + led] = strip[1, led];
                    strip[2, (fan + 1) * 8 + led] = strip[2, led];
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
        return frame;
    }
    internal int InfReflect(int zone, int fans, int bright, int direction, int attach)
    {
        byte[,] strip = new byte[3, 176];
        byte[] fadeTable = new byte[8] { 255, 192, 168, 128, 96, 64, 32, 16 };
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
        zone = ((zone <= 2) ? zone : 0);
        fans = ((fans == 0 || fans > 4) ? 4 : fans);
        direction = ((direction <= 1) ? direction : 0);
        attach = ((attach <= 1) ? attach : 0);
        int halfLeds = zoneLedCountByFans[zone, fans - 1] / 2;
        int frame = 0;
        for (int colorIdx = 0; colorIdx < 4; colorIdx++)
        {
            for (int pass = 0; pass < 2; pass++)
            {
                for (int head = 0; head < halfLeds + fans + fans - 1; head++)
                {
                    for (int led = 0; led < halfLeds; led++)
                    {
                        int green;
                        int blue;
                        int red = (green = (blue = 0));
                        if (led <= head && led + fans + fans > head && pass == 0)
                        {
                            red = palette[zone, colorIdx, 0] * fadeTable[head - led] >> 8;
                            green = palette[zone, colorIdx, 1] * fadeTable[head - led] >> 8;
                            blue = palette[zone, colorIdx, 2] * fadeTable[head - led] >> 8;
                        }
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
                        int green;
                        int blue;
                        int red = (green = (blue = 0));
                        if (led <= head && led + fans + fans > head && pass == 0)
                        {
                            red = palette[zone, colorIdx, 0] * fadeTable[head - led] >> 8;
                            green = palette[zone, colorIdx, 1] * fadeTable[head - led] >> 8;
                            blue = palette[zone, colorIdx, 2] * fadeTable[head - led] >> 8;
                        }
                        int stripIdx = halfLeds - led - 1 + halfLeds;
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
        }
        return frame;
    }
}
