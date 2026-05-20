using System.Collections.Generic;

namespace Qos.Service.Peripherals.Keeb.Hid.KeyAssignment
{
    public static class KeyAssignmentFunctionList
    {
        public static readonly List<KeyFunction> STANDARD_KEY_LIST =
        [
            KeyFunction.A, KeyFunction.B, KeyFunction.C, KeyFunction.D, KeyFunction.E, KeyFunction.F, KeyFunction.G, KeyFunction.H, KeyFunction.I, KeyFunction.J, KeyFunction.K, KeyFunction.L, KeyFunction.M, KeyFunction.N, KeyFunction.O, KeyFunction.P, KeyFunction.Q, KeyFunction.R, KeyFunction.S, KeyFunction.T, KeyFunction.U, KeyFunction.V, KeyFunction.W, KeyFunction.X, KeyFunction.Y, KeyFunction.Z,
            KeyFunction.Number1, KeyFunction.Number2, KeyFunction.Number3, KeyFunction.Number4, KeyFunction.Number5, KeyFunction.Number6, KeyFunction.Number7, KeyFunction.Number8, KeyFunction.Number9, KeyFunction.Number0,
            KeyFunction.Return, KeyFunction.Escape, KeyFunction.Backspace, KeyFunction.Tab, KeyFunction.Space, KeyFunction.Minus, KeyFunction.Equals,
            KeyFunction.LeftBracket, KeyFunction.RightBracket, KeyFunction.Backslash, KeyFunction.NonUsPound, KeyFunction.Semicolon, KeyFunction.Quote, KeyFunction.Backtick, KeyFunction.Comma, KeyFunction.Period, KeyFunction.Slash, KeyFunction.CapsLock,
            KeyFunction.F1, KeyFunction.F2, KeyFunction.F3, KeyFunction.F4, KeyFunction.F5, KeyFunction.F6, KeyFunction.F7, KeyFunction.F8, KeyFunction.F9, KeyFunction.F10, KeyFunction.F11, KeyFunction.F12,
            KeyFunction.PrintScreen, KeyFunction.ScrollLock, KeyFunction.Pause,
            KeyFunction.Insert, KeyFunction.Home, KeyFunction.PageUp,
            KeyFunction.Delete, KeyFunction.End, KeyFunction.PageDown,
            KeyFunction.RightArrow, KeyFunction.LeftArrow, KeyFunction.DownArrow, KeyFunction.UpArrow,
            KeyFunction.NumLock, KeyFunction.KeypadSlash, KeyFunction.KeypadAsterisk, KeyFunction.KeypadMinus, KeyFunction.KeypadPlus, KeyFunction.KeypadEnter,
            KeyFunction.Keypad1End, KeyFunction.Keypad2DownArrow, KeyFunction.Keypad3PageDown,
            KeyFunction.Keypad4LeftArrow, KeyFunction.Keypad5, KeyFunction.Keypad6RightArrow,
            KeyFunction.Keypad7Home, KeyFunction.Keypad8UpArrow, KeyFunction.Keypad9PageUp, KeyFunction.Keypad0Insert,
            KeyFunction.KeypadPeriodDelete, KeyFunction.NonUsBackslash, KeyFunction.Application, KeyFunction.KeyboardPower, KeyFunction.KeypadEqual,
            KeyFunction.F13, KeyFunction.F14, KeyFunction.F15, KeyFunction.F16, KeyFunction.F17, KeyFunction.F18, KeyFunction.F19, KeyFunction.F20, KeyFunction.F21, KeyFunction.F22, KeyFunction.F23, KeyFunction.F24,
            KeyFunction.KeypadComma,
            KeyFunction.International1,
            KeyFunction.International2,
            KeyFunction.International3,
            KeyFunction.International4,
            KeyFunction.International5,
            KeyFunction.Lang1,
            KeyFunction.LeftControl, KeyFunction.LeftShift, KeyFunction.LeftAlt, KeyFunction.LeftGUI,
            KeyFunction.RightControl, KeyFunction.RightShift, KeyFunction.RightAlt, KeyFunction.RightGUI,
            KeyFunction.CombinationKey,
            KeyFunction.PassThrough,
            KeyFunction.None,
            KeyFunction.Europe1, KeyFunction.Europe2
        ];

        public static readonly List<KeyFunction> MOUSE_KEY_LIST = new()
        {
            KeyFunction.MouseLButton,
            KeyFunction.MouseRButton,
            KeyFunction.MouseMButton,
            KeyFunction.MouseB4Button,
            KeyFunction.MouseB5Button,
            KeyFunction.MouseWheelUp,
            KeyFunction.MouseWheelDown,
            KeyFunction.MouseACPanLeft,
            KeyFunction.MouseACPanRight,
            KeyFunction.MouseXPanLeft,
            KeyFunction.MouseXPanRight,
            KeyFunction.MouseXPanUp,
            KeyFunction.MouseXPanDown,
        };

        public static readonly List<KeyFunction> MEDIA_KEY_LIST = new()
        {
            KeyFunction.FastForward,
            KeyFunction.Rewind,
            KeyFunction.ScanNextTrack,
            KeyFunction.ScanPreviousTrack,
            KeyFunction.Stop,
            KeyFunction.PlayAndPause,
            KeyFunction.Mute,
            KeyFunction.VolumeUp,
            KeyFunction.VolumeDown,
        };

        public static readonly List<KeyFunction> SYSTEM_MEDIA_KEY_LIST = new()
        {
            KeyFunction.MediaSelect,
            KeyFunction.Mail,
            KeyFunction.Calculator,
            KeyFunction.MyComputer
        };

        public static readonly List<KeyFunction> WEB_MEDIA_KEY_LIST = new()
        {
            KeyFunction.WebSearch,
            KeyFunction.WebHome,
            KeyFunction.WebBack,
            KeyFunction.WebForward,
            KeyFunction.WebStop,
            KeyFunction.WebRefresh,
            KeyFunction.WebFavorite,
        };

        public static readonly List<KeyFunction> MACRO_KEY_LIST = new()
        {
            KeyFunction.Macro1,
            KeyFunction.Macro2,
            KeyFunction.Macro3,
            KeyFunction.Macro4,
            KeyFunction.Macro5,
            KeyFunction.Macro6,
            KeyFunction.Macro7,
            KeyFunction.Macro8,
            KeyFunction.Macro9,
            KeyFunction.Macro10,
            KeyFunction.Macro11,
            KeyFunction.Macro12,
            KeyFunction.Macro13,
            KeyFunction.Macro14,
            KeyFunction.Macro15,
            KeyFunction.Macro16,
        };

        public static readonly List<KeyFunction> SYSTEM_KEY_LIST = new()
        {
            KeyFunction.Power,
            KeyFunction.Sleep,
            KeyFunction.Wake
        };

        public static readonly List<KeyFunction> LAYER_KEY_LIST = new()
        {
            KeyFunction.MOSwitch,
            KeyFunction.TGSwitch,
            KeyFunction.TOSwitch,
            KeyFunction.DFSwitch
        };

        public static readonly List<KeyFunction> PROFILE_KEY_LIST = new()
        {
            KeyFunction.ProfilePlus,
            KeyFunction.ProfileMinus,
            KeyFunction.ProfilePlusLoop,
            KeyFunction.ProfileValue
        };

        public static readonly List<KeyFunction> RGB_KEY_LIST = new()
        {
            KeyFunction.RGBOnOff,
            KeyFunction.RGBEffectLoop,
            KeyFunction.RGBEffectValue,

            KeyFunction.BrightnessIncrease,
            KeyFunction.BrightnessDecrease,

            KeyFunction.SpeedIncrease,
            KeyFunction.SpeedDecrease,
            KeyFunction.SpeedLoop,

            KeyFunction.ColorIncrease,
            KeyFunction.ColorDecrease,
            KeyFunction.ColorLoop,

            KeyFunction.DirectionLoop,
            KeyFunction.DirectionValue
        };

        public static readonly List<KeyFunction> SOFTWARE_KEY_LIST = new()
        {
            KeyFunction.SoftwareControl
        };

        /// <summary>
        /// Key assignment mode vs Key function list
        /// </summary>
        public static readonly Dictionary<KeyAssignmentMode, List<KeyFunction>> KEYFUNCTION_CATEGORIES = new()
        {
            { KeyAssignmentMode.StandardKey, STANDARD_KEY_LIST },
            { KeyAssignmentMode.MouseKey, MOUSE_KEY_LIST },
            { KeyAssignmentMode.MediaKey, MEDIA_KEY_LIST },
            { KeyAssignmentMode.SystemMediaKey, SYSTEM_MEDIA_KEY_LIST },
            { KeyAssignmentMode.WebMediaKey, WEB_MEDIA_KEY_LIST },
            { KeyAssignmentMode.SystemKey, SYSTEM_KEY_LIST },
            { KeyAssignmentMode.MacroKey, MACRO_KEY_LIST },
            { KeyAssignmentMode.ProfileKey, PROFILE_KEY_LIST },
            { KeyAssignmentMode.LayerKey, LAYER_KEY_LIST },
            { KeyAssignmentMode.RGBKey, RGB_KEY_LIST },
            { KeyAssignmentMode.SoftwareKey, SOFTWARE_KEY_LIST },
        };
    }
}
