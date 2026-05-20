using System.Collections.Generic;

namespace Qos.Service.Peripherals.Keeb.Hid.Macros
{
    public static class MacroFunctionList
    {
        public static readonly List<MacroKeyCode> MACRO_STANDARD_KEY = new()
        {
            MacroKeyCode.A, MacroKeyCode.B, MacroKeyCode.C, MacroKeyCode.D, MacroKeyCode.E, MacroKeyCode.F, MacroKeyCode.G, MacroKeyCode.H, MacroKeyCode.I, MacroKeyCode.J, MacroKeyCode.K, MacroKeyCode.L, MacroKeyCode.M, MacroKeyCode.N, MacroKeyCode.O, MacroKeyCode.P, MacroKeyCode.Q, MacroKeyCode.R, MacroKeyCode.S, MacroKeyCode.T, MacroKeyCode.U, MacroKeyCode.V, MacroKeyCode.W, MacroKeyCode.X, MacroKeyCode.Y, MacroKeyCode.Z,
            MacroKeyCode.Number1, MacroKeyCode.Number2, MacroKeyCode.Number3, MacroKeyCode.Number4, MacroKeyCode.Number5, MacroKeyCode.Number6, MacroKeyCode.Number7, MacroKeyCode.Number8, MacroKeyCode.Number9, MacroKeyCode.Number0,
            MacroKeyCode.Return, MacroKeyCode.Escape, MacroKeyCode.Backspace, MacroKeyCode.Tab, MacroKeyCode.Space, MacroKeyCode.Minus, MacroKeyCode.Equals,
            MacroKeyCode.LeftBracket, MacroKeyCode.RightBracket, MacroKeyCode.Backslash, MacroKeyCode.NonUsPound, MacroKeyCode.Semicolon, MacroKeyCode.Quote, MacroKeyCode.Backtick, MacroKeyCode.Comma, MacroKeyCode.Period, MacroKeyCode.Slash, MacroKeyCode.CapsLock,
            MacroKeyCode.F1, MacroKeyCode.F2, MacroKeyCode.F3, MacroKeyCode.F4, MacroKeyCode.F5, MacroKeyCode.F6, MacroKeyCode.F7, MacroKeyCode.F8, MacroKeyCode.F9, MacroKeyCode.F10, MacroKeyCode.F11, MacroKeyCode.F12,
            MacroKeyCode.PrintScreen, MacroKeyCode.ScrollLock, MacroKeyCode.Pause,
            MacroKeyCode.Insert, MacroKeyCode.Home, MacroKeyCode.PageUp,
            MacroKeyCode.Delete, MacroKeyCode.End, MacroKeyCode.PageDown,
            MacroKeyCode.RightArrow, MacroKeyCode.LeftArrow, MacroKeyCode.DownArrow, MacroKeyCode.UpArrow,
            MacroKeyCode.NumLock, MacroKeyCode.KeypadSlash, MacroKeyCode.KeypadAsterisk, MacroKeyCode.KeypadMinus, MacroKeyCode.KeypadPlus, MacroKeyCode.KeypadEnter,
            MacroKeyCode.Keypad1End, MacroKeyCode.Keypad2DownArrow, MacroKeyCode.Keypad3PageDown,
            MacroKeyCode.Keypad4LeftArrow, MacroKeyCode.Keypad5, MacroKeyCode.Keypad6RightArrow,
            MacroKeyCode.Keypad7Home, MacroKeyCode.Keypad8UpArrow, MacroKeyCode.Keypad9PageUp, MacroKeyCode.Keypad0Insert,
            MacroKeyCode.KeypadPeriodDelete, MacroKeyCode.NonUsBackslash, MacroKeyCode.Application, MacroKeyCode.KeypadEqual,
            MacroKeyCode.F13, MacroKeyCode.F14, MacroKeyCode.F15, MacroKeyCode.F16, MacroKeyCode.F17, MacroKeyCode.F18, MacroKeyCode.F19, MacroKeyCode.F20, MacroKeyCode.F21, MacroKeyCode.F22, MacroKeyCode.F23, MacroKeyCode.F24,
            MacroKeyCode.KeypadComma,
            MacroKeyCode.International1,
            MacroKeyCode.International2,
            MacroKeyCode.International3,
            MacroKeyCode.International4,
            MacroKeyCode.International5,
            MacroKeyCode.Lang1,
            MacroKeyCode.LeftControl, MacroKeyCode.LeftShift, MacroKeyCode.LeftAlt, MacroKeyCode.LeftGUI,
            MacroKeyCode.RightControl, MacroKeyCode.RightShift, MacroKeyCode.RightAlt, MacroKeyCode.RightGUI,
            MacroKeyCode.CombinationKey,
        };

        public static readonly List<MacroKeyCode> MACRO_MOUSE_KEY = new()
        {
            MacroKeyCode.MouseLButton,
            MacroKeyCode.MouseRButton,
            MacroKeyCode.MouseMButton,
            MacroKeyCode.MouseB4Button,
            MacroKeyCode.MouseB5Button,
            MacroKeyCode.MouseWheelUp,
            MacroKeyCode.MouseWheelDown,
            MacroKeyCode.MouseACPanLeft,
        };

        public readonly static Dictionary<MacroKeyCategory, List<MacroKeyCode>> MACROKEY_CATEGORIES = new()
        {
            { MacroKeyCategory.StandardKey, MACRO_STANDARD_KEY },
            { MacroKeyCategory.MouseKey, MACRO_MOUSE_KEY },
        };
    }
}
