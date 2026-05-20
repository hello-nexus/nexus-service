using Qos.Service.Peripherals.Keeb.Hid.KeyAssignment;

namespace Qos.Service.Peripherals.Keeb.Hid.Layout
{
    public static class KeyAssignmentDefaults
    {
        public static readonly int[][] ANSI_COMMAND_INDEX =
        [
            [2],
            [78, 79, 80, 99, 101],
            [1, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16, 17],
            [22, 23, 24, 25, 26, 27, 28, 29, 30, 31, 32, 33, 34, 35, 36, 37, 38],
            [43, 44, 45, 46, 47, 48, 49, 50, 51, 52, 53, 54, 55, 56, 57, 58, 59],
            [64, 65, 66, 67, 68, 69, 70, 71, 72, 73, 74, 75, 77],
            [85, 87, 88, 89, 90, 91, 92, 93, 94, 95, 96, 98, 100],
            [106, 107, 108, 112, 116, 117, 118, 119, 120, 121, 122],
        ];

        /// <summary>Allocates a fresh ANSI layer-1 default key map on each call; callers should assign to a local variable.</summary>
        public static Key[][] ANSI_DEFAULT_LAYER1 =>
        [
            [new(KeyFunction.RGBEffectLoop, null, 0)],
            [new(KeyFunction.Stop, null, 0), new(KeyFunction.ScanPreviousTrack, null, 0), new(KeyFunction.PlayAndPause, null, 0), new(KeyFunction.ScanNextTrack, null, 0), new(KeyFunction.Mute, null, 0)],
            [new(KeyFunction.Escape, null, 0), new(KeyFunction.F1, null, 0),new(KeyFunction.F2, null, 0),new(KeyFunction.F3, null, 0),new(KeyFunction.F4, null, 0),new(KeyFunction.F5, null, 0),new(KeyFunction.F6, null, 0),new(KeyFunction.F7, null, 0),new(KeyFunction.F8, null, 0),new(KeyFunction.F9, null, 0),new(KeyFunction.F10, null, 0),new(KeyFunction.F11, null, 0),new(KeyFunction.F12, null, 0),new(KeyFunction.PrintScreen, null, 0),new(KeyFunction.ScrollLock, null, 0),new(KeyFunction.Pause, null, 0)],
            [new(KeyFunction.Backtick, null, 0), new(KeyFunction.Number1, null, 0),new(KeyFunction.Number2, null, 0),new(KeyFunction.Number3, null, 0),new(KeyFunction.Number4, null, 0),new(KeyFunction.Number5, null, 0),new(KeyFunction.Number6, null, 0),new(KeyFunction.Number7, null, 0),new(KeyFunction.Number8, null, 0),new(KeyFunction.Number9, null, 0),new(KeyFunction.Number0, null, 0),new(KeyFunction.Minus, null, 0),new(KeyFunction.Equals, null, 0),new(KeyFunction.Backspace, null, 0),new(KeyFunction.Insert, null, 0),new(KeyFunction.Home, null, 0),new(KeyFunction.PageUp, null, 0)],
            [new(KeyFunction.Tab, null, 0), new(KeyFunction.Q, null, 0),new(KeyFunction.W, null, 0),new(KeyFunction.E, null, 0),new(KeyFunction.R, null, 0),new(KeyFunction.T, null, 0),new(KeyFunction.Y, null, 0),new(KeyFunction.U, null, 0),new(KeyFunction.I, null, 0),new(KeyFunction.O, null, 0),new(KeyFunction.P, null, 0),new(KeyFunction.LeftBracket, null, 0),new(KeyFunction.RightBracket, null, 0),new(KeyFunction.Backslash, null, 0),new(KeyFunction.Delete, null, 0),new(KeyFunction.End, null, 0),new(KeyFunction.PageDown, null, 0)],
            [new(KeyFunction.CapsLock, null, 0), new(KeyFunction.A, null, 0),new(KeyFunction.S, null, 0),new(KeyFunction.D, null, 0),new(KeyFunction.F, null, 0),new(KeyFunction.G, null, 0),new(KeyFunction.H, null, 0),new(KeyFunction.J, null, 0),new(KeyFunction.K, null, 0),new(KeyFunction.L, null, 0),new(KeyFunction.Semicolon, null, 0),new(KeyFunction.Quote, null, 0),new(KeyFunction.Return, null, 0)],
            [new(KeyFunction.LeftShift, null, 0), new(KeyFunction.Z, null, 0),new(KeyFunction.X, null, 0),new(KeyFunction.C, null, 0),new(KeyFunction.V, null, 0),new(KeyFunction.B, null, 0),new(KeyFunction.N, null, 0),new(KeyFunction.M, null, 0),new(KeyFunction.Comma, null, 0),new(KeyFunction.Period, null, 0),new(KeyFunction.Slash, null, 0), new(KeyFunction.RightShift, null, 0), new(KeyFunction.UpArrow, null, 0)],
            [new(KeyFunction.LeftControl, null, 0), new(KeyFunction.LeftGUI, null, 0),new(KeyFunction.LeftAlt, null, 0),new(KeyFunction.Space, null, 0),new(KeyFunction.RightAlt, null, 0),new(KeyFunction.MOSwitch, new byte[] {0x01}, 0),new(KeyFunction.RightGUI, null, 0),new(KeyFunction.RightControl, null, 0),new(KeyFunction.LeftArrow, null, 0),new(KeyFunction.DownArrow, null, 0),new(KeyFunction.RightArrow, null, 0)],
        ];

        /// <summary>Allocates a fresh ANSI non-layer-1 default key map on each call; callers should assign to a local variable.</summary>
        public static Key[][] ANSI_DEFAULT_LAYER_OTHERS =>
        [
            [new(KeyFunction.PassThrough, null, 0)],
            [new(KeyFunction.PassThrough, null, 0), new(KeyFunction.PassThrough, null, 0), new(KeyFunction.PassThrough, null, 0), new(KeyFunction.PassThrough, null, 0), new(KeyFunction.PassThrough, null, 0)],
            [new(KeyFunction.PassThrough, null, 0), new(KeyFunction.PassThrough, null, 0), new(KeyFunction.PassThrough, null, 0),new(KeyFunction.PassThrough, null, 0),new(KeyFunction.PassThrough, null, 0),new(KeyFunction.PassThrough, null, 0),new(KeyFunction.PassThrough, null, 0),new(KeyFunction.PassThrough, null, 0),new(KeyFunction.PassThrough, null, 0),new(KeyFunction.PassThrough, null, 0),new(KeyFunction.PassThrough, null, 0), new(KeyFunction.PassThrough, null, 0), new(KeyFunction.PassThrough, null, 0), new(KeyFunction.PassThrough, null, 0),new(KeyFunction.PassThrough, null, 0),new(KeyFunction.PassThrough, null, 0)],
            [new(KeyFunction.PassThrough, null, 0), new(KeyFunction.PassThrough, null, 0), new(KeyFunction.PassThrough, null, 0),new(KeyFunction.PassThrough, null, 0),new(KeyFunction.PassThrough, null, 0),new(KeyFunction.PassThrough, null, 0),new(KeyFunction.PassThrough, null, 0),new(KeyFunction.PassThrough, null, 0),new(KeyFunction.PassThrough, null, 0),new(KeyFunction.PassThrough, null, 0),new(KeyFunction.PassThrough, null, 0), new(KeyFunction.PassThrough, null, 0), new(KeyFunction.PassThrough, null, 0), new(KeyFunction.PassThrough, null, 0),new(KeyFunction.PassThrough, null, 0),new(KeyFunction.PassThrough, null, 0),new(KeyFunction.PassThrough, null, 0)],
            [new(KeyFunction.PassThrough, null, 0), new(KeyFunction.PassThrough, null, 0), new(KeyFunction.PassThrough, null, 0),new(KeyFunction.PassThrough, null, 0),new(KeyFunction.PassThrough, null, 0),new(KeyFunction.PassThrough, null, 0),new(KeyFunction.PassThrough, null, 0),new(KeyFunction.PassThrough, null, 0),new(KeyFunction.PassThrough, null, 0),new(KeyFunction.PassThrough, null, 0),new(KeyFunction.PassThrough, null, 0), new(KeyFunction.PassThrough, null, 0), new(KeyFunction.PassThrough, null, 0), new(KeyFunction.PassThrough, null, 0),new(KeyFunction.PassThrough, null, 0),new(KeyFunction.PassThrough, null, 0),new(KeyFunction.PassThrough, null, 0)],
            [new(KeyFunction.PassThrough, null, 0), new(KeyFunction.PassThrough, null, 0), new(KeyFunction.PassThrough, null, 0),new(KeyFunction.PassThrough, null, 0),new(KeyFunction.PassThrough, null, 0),new(KeyFunction.PassThrough, null, 0),new(KeyFunction.PassThrough, null, 0),new(KeyFunction.PassThrough, null, 0),new(KeyFunction.PassThrough, null, 0),new(KeyFunction.PassThrough, null, 0),new(KeyFunction.PassThrough, null, 0), new(KeyFunction.PassThrough, null, 0), new(KeyFunction.PassThrough, null, 0)],
            [new(KeyFunction.PassThrough, null, 0), new(KeyFunction.PassThrough, null, 0), new(KeyFunction.PassThrough, null, 0),new(KeyFunction.PassThrough, null, 0),new(KeyFunction.PassThrough, null, 0),new(KeyFunction.PassThrough, null, 0),new(KeyFunction.PassThrough, null, 0),new(KeyFunction.PassThrough, null, 0),new(KeyFunction.PassThrough, null, 0),new(KeyFunction.PassThrough, null, 0),new(KeyFunction.PassThrough, null, 0), new(KeyFunction.PassThrough, null, 0), new(KeyFunction.PassThrough, null, 0)],
            [new(KeyFunction.PassThrough, null, 0), new(KeyFunction.PassThrough, null, 0), new(KeyFunction.PassThrough, null, 0),new(KeyFunction.PassThrough, null, 0),new(KeyFunction.PassThrough, null, 0),new(KeyFunction.PassThrough, null, 0),new(KeyFunction.PassThrough, null, 0),new(KeyFunction.PassThrough, null, 0),new(KeyFunction.PassThrough, null, 0),new(KeyFunction.PassThrough, null, 0),new(KeyFunction.PassThrough, null, 0)],
        ];

        public static readonly int[][] ISO_COMMAND_INDEX =
        [
            [2],
            [78, 79, 80, 99, 101],
            [1, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16, 17],
            [22, 23, 24, 25, 26, 27, 28, 29, 30, 31, 32, 33, 34, 35, 36, 37, 38],
            [43, 44, 45, 46, 47, 48, 49, 50, 51, 52, 53, 54, 55, 77, 57, 58, 59],
            [64, 65, 66, 67, 68, 69, 70, 71, 72, 73, 74, 75, 76],
            [85, 86, 87, 88, 89, 90, 91, 92, 93, 94, 95, 96, 98, 100],
            [106, 107, 108, 112, 116, 117, 118, 119, 120, 121, 122],
        ];

        /// <summary>Allocates a fresh ISO layer-1 default key map on each call; callers should assign to a local variable.</summary>
        public static Key[][] ISO_DEFAULT_LAYER1 =>
        [
            [new(KeyFunction.RGBEffectLoop, null, 0)],
            [new(KeyFunction.Stop, null, 0), new(KeyFunction.ScanPreviousTrack, null, 0), new(KeyFunction.PlayAndPause, null, 0), new(KeyFunction.ScanNextTrack, null, 0), new(KeyFunction.Mute, null, 0)],
            [new(KeyFunction.Escape, null, 0), new(KeyFunction.F1, null, 0),new(KeyFunction.F2, null, 0),new(KeyFunction.F3, null, 0),new(KeyFunction.F4, null, 0),new(KeyFunction.F5, null, 0),new(KeyFunction.F6, null, 0),new(KeyFunction.F7, null, 0),new(KeyFunction.F8, null, 0),new(KeyFunction.F9, null, 0),new(KeyFunction.F10, null, 0),new(KeyFunction.F11, null, 0),new(KeyFunction.F12, null, 0),new(KeyFunction.PrintScreen, null, 0),new(KeyFunction.ScrollLock, null, 0),new(KeyFunction.Pause, null, 0)],
            [new(KeyFunction.Backtick, null, 0), new(KeyFunction.Number1, null, 0),new(KeyFunction.Number2, null, 0),new(KeyFunction.Number3, null, 0),new(KeyFunction.Number4, null, 0),new(KeyFunction.Number5, null, 0),new(KeyFunction.Number6, null, 0),new(KeyFunction.Number7, null, 0),new(KeyFunction.Number8, null, 0),new(KeyFunction.Number9, null, 0),new(KeyFunction.Number0, null, 0),new(KeyFunction.Minus, null, 0),new(KeyFunction.Equals, null, 0),new(KeyFunction.Backspace, null, 0),new(KeyFunction.Insert, null, 0),new(KeyFunction.Home, null, 0),new(KeyFunction.PageUp, null, 0)],
            [new(KeyFunction.Tab, null, 0), new(KeyFunction.Q, null, 0),new(KeyFunction.W, null, 0),new(KeyFunction.E, null, 0),new(KeyFunction.R, null, 0),new(KeyFunction.T, null, 0),new(KeyFunction.Y, null, 0),new(KeyFunction.U, null, 0),new(KeyFunction.I, null, 0),new(KeyFunction.O, null, 0),new(KeyFunction.P, null, 0),new(KeyFunction.LeftBracket, null, 0),new(KeyFunction.RightBracket, null, 0), new(KeyFunction.Return, null, 0),new(KeyFunction.Delete, null, 0),new(KeyFunction.End, null, 0),new(KeyFunction.PageDown, null, 0)],
            [new(KeyFunction.CapsLock, null, 0), new(KeyFunction.A, null, 0),new(KeyFunction.S, null, 0),new(KeyFunction.D, null, 0),new(KeyFunction.F, null, 0),new(KeyFunction.G, null, 0),new(KeyFunction.H, null, 0),new(KeyFunction.J, null, 0),new(KeyFunction.K, null, 0),new(KeyFunction.L, null, 0),new(KeyFunction.Semicolon, null, 0),new(KeyFunction.Quote, null, 0), new(KeyFunction.Europe1, null, 0)],
            [new(KeyFunction.LeftShift, null, 0), new(KeyFunction.Europe2, null, 0), new(KeyFunction.Z, null, 0),new(KeyFunction.X, null, 0),new(KeyFunction.C, null, 0),new(KeyFunction.V, null, 0),new(KeyFunction.B, null, 0),new(KeyFunction.N, null, 0),new(KeyFunction.M, null, 0),new(KeyFunction.Comma, null, 0),new(KeyFunction.Period, null, 0),new(KeyFunction.Slash, null, 0), new(KeyFunction.RightShift, null, 0), new(KeyFunction.UpArrow, null, 0)],
            [new(KeyFunction.LeftControl, null, 0), new(KeyFunction.LeftGUI, null, 0),new(KeyFunction.LeftAlt, null, 0),new(KeyFunction.Space, null, 0),new(KeyFunction.RightAlt, null, 0),new(KeyFunction.RightGUI, null, 0),new(KeyFunction.RightGUI, null, 0),new(KeyFunction.RightControl, null, 0),new(KeyFunction.LeftArrow, null, 0),new(KeyFunction.DownArrow, null, 0),new(KeyFunction.RightArrow, null, 0)],
        ];

        /// <summary>Allocates a fresh ISO non-layer-1 default key map on each call; callers should assign to a local variable.</summary>
        public static Key[][] ISO_DEFAULT_LAYER_OTHERS =>
        [
            [new(KeyFunction.PassThrough, null, 0)],
            [new(KeyFunction.PassThrough, null, 0), new(KeyFunction.PassThrough, null, 0), new(KeyFunction.PassThrough, null, 0), new(KeyFunction.PassThrough, null, 0), new(KeyFunction.PassThrough, null, 0)],
            [new(KeyFunction.PassThrough, null, 0), new(KeyFunction.PassThrough, null, 0), new(KeyFunction.PassThrough, null, 0),new(KeyFunction.PassThrough, null, 0),new(KeyFunction.PassThrough, null, 0),new(KeyFunction.PassThrough, null, 0),new(KeyFunction.PassThrough, null, 0),new(KeyFunction.PassThrough, null, 0),new(KeyFunction.PassThrough, null, 0),new(KeyFunction.PassThrough, null, 0),new(KeyFunction.PassThrough, null, 0), new(KeyFunction.PassThrough, null, 0), new(KeyFunction.PassThrough, null, 0), new(KeyFunction.PassThrough, null, 0),new(KeyFunction.PassThrough, null, 0),new(KeyFunction.PassThrough, null, 0)],
            [new(KeyFunction.PassThrough, null, 0), new(KeyFunction.PassThrough, null, 0), new(KeyFunction.PassThrough, null, 0),new(KeyFunction.PassThrough, null, 0),new(KeyFunction.PassThrough, null, 0),new(KeyFunction.PassThrough, null, 0),new(KeyFunction.PassThrough, null, 0),new(KeyFunction.PassThrough, null, 0),new(KeyFunction.PassThrough, null, 0),new(KeyFunction.PassThrough, null, 0),new(KeyFunction.PassThrough, null, 0), new(KeyFunction.PassThrough, null, 0), new(KeyFunction.PassThrough, null, 0), new(KeyFunction.PassThrough, null, 0),new(KeyFunction.PassThrough, null, 0),new(KeyFunction.PassThrough, null, 0),new(KeyFunction.PassThrough, null, 0)],
            [new(KeyFunction.PassThrough, null, 0), new(KeyFunction.PassThrough, null, 0), new(KeyFunction.PassThrough, null, 0),new(KeyFunction.PassThrough, null, 0),new(KeyFunction.PassThrough, null, 0),new(KeyFunction.PassThrough, null, 0),new(KeyFunction.PassThrough, null, 0),new(KeyFunction.PassThrough, null, 0),new(KeyFunction.PassThrough, null, 0),new(KeyFunction.PassThrough, null, 0),new(KeyFunction.PassThrough, null, 0), new(KeyFunction.PassThrough, null, 0), new(KeyFunction.PassThrough, null, 0), new(KeyFunction.PassThrough, null, 0),new(KeyFunction.PassThrough, null, 0),new(KeyFunction.PassThrough, null, 0),new(KeyFunction.PassThrough, null, 0)],
            [new(KeyFunction.PassThrough, null, 0), new(KeyFunction.PassThrough, null, 0), new(KeyFunction.PassThrough, null, 0),new(KeyFunction.PassThrough, null, 0),new(KeyFunction.PassThrough, null, 0),new(KeyFunction.PassThrough, null, 0),new(KeyFunction.PassThrough, null, 0),new(KeyFunction.PassThrough, null, 0),new(KeyFunction.PassThrough, null, 0),new(KeyFunction.PassThrough, null, 0),new(KeyFunction.PassThrough, null, 0), new(KeyFunction.PassThrough, null, 0), new(KeyFunction.PassThrough, null, 0)],
            [new(KeyFunction.PassThrough, null, 0), new(KeyFunction.PassThrough, null, 0), new(KeyFunction.PassThrough, null, 0), new(KeyFunction.PassThrough, null, 0),new(KeyFunction.PassThrough, null, 0),new(KeyFunction.PassThrough, null, 0),new(KeyFunction.PassThrough, null, 0),new(KeyFunction.PassThrough, null, 0),new(KeyFunction.PassThrough, null, 0),new(KeyFunction.PassThrough, null, 0),new(KeyFunction.PassThrough, null, 0),new(KeyFunction.PassThrough, null, 0), new(KeyFunction.PassThrough, null, 0), new(KeyFunction.PassThrough, null, 0)],
            [new(KeyFunction.PassThrough, null, 0), new(KeyFunction.PassThrough, null, 0), new(KeyFunction.PassThrough, null, 0),new(KeyFunction.PassThrough, null, 0),new(KeyFunction.PassThrough, null, 0),new(KeyFunction.PassThrough, null, 0),new(KeyFunction.PassThrough, null, 0),new(KeyFunction.PassThrough, null, 0),new(KeyFunction.PassThrough, null, 0),new(KeyFunction.PassThrough, null, 0),new(KeyFunction.PassThrough, null, 0)],
        ];
    }
}
