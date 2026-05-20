using System;
using System.Collections.Generic;
using Qos.Service.Peripherals.Keeb.Hid.Enums;
using Qos.Service.Peripherals.Keeb.Hid.Macros;

namespace Qos.Service.Peripherals.Keeb.Hid.KeyAssignment
{
    public static class KeyFunctionByteDictionary
    {
        public static readonly Dictionary<KeyFunction, byte[]> STATIC_KEY_BYTES = new()
        {
            #region StandardKey
            { KeyFunction.A, new byte[4] { 0x04, 0x00, 0x00, 0x01 } },
            { KeyFunction.B, new byte[4] { 0x05, 0x00, 0x00, 0x01 } },
            { KeyFunction.C, new byte[4] { 0x06, 0x00, 0x00, 0x01 } },
            { KeyFunction.D, new byte[4] { 0x07, 0x00, 0x00, 0x01 } },
            { KeyFunction.E, new byte[4] { 0x08, 0x00, 0x00, 0x01 } },
            { KeyFunction.F, new byte[4] { 0x09, 0x00, 0x00, 0x01 } },
            { KeyFunction.G, new byte[4] { 0x0A, 0x00, 0x00, 0x01 } },
            { KeyFunction.H, new byte[4] { 0x0B, 0x00, 0x00, 0x01 } },
            { KeyFunction.I, new byte[4] { 0x0C, 0x00, 0x00, 0x01 } },
            { KeyFunction.J, new byte[4] { 0x0D, 0x00, 0x00, 0x01 } },
            { KeyFunction.K, new byte[4] { 0x0E, 0x00, 0x00, 0x01 } },
            { KeyFunction.L, new byte[4] { 0x0F, 0x00, 0x00, 0x01 } },
            { KeyFunction.M, new byte[4] { 0x10, 0x00, 0x00, 0x01 } },
            { KeyFunction.N, new byte[4] { 0x11, 0x00, 0x00, 0x01 } },
            { KeyFunction.O, new byte[4] { 0x12, 0x00, 0x00, 0x01 } },
            { KeyFunction.P, new byte[4] { 0x13, 0x00, 0x00, 0x01 } },
            { KeyFunction.Q, new byte[4] { 0x14, 0x00, 0x00, 0x01 } },
            { KeyFunction.R, new byte[4] { 0x15, 0x00, 0x00, 0x01 } },
            { KeyFunction.S, new byte[4] { 0x16, 0x00, 0x00, 0x01 } },
            { KeyFunction.T, new byte[4] { 0x17, 0x00, 0x00, 0x01 } },
            { KeyFunction.U, new byte[4] { 0x18, 0x00, 0x00, 0x01 } },
            { KeyFunction.V, new byte[4] { 0x19, 0x00, 0x00, 0x01 } },
            { KeyFunction.W, new byte[4] { 0x1A, 0x00, 0x00, 0x01 } },
            { KeyFunction.X, new byte[4] { 0x1B, 0x00, 0x00, 0x01 } },
            { KeyFunction.Y, new byte[4] { 0x1C, 0x00, 0x00, 0x01 } },
            { KeyFunction.Z, new byte[4] { 0x1D, 0x00, 0x00, 0x01 } },

            { KeyFunction.Number1, new byte[4] { 0x1E, 0x00, 0x00, 0x01 } },
            { KeyFunction.Number2, new byte[4] { 0x1F, 0x00, 0x00, 0x01 } },
            { KeyFunction.Number3, new byte[4] { 0x20, 0x00, 0x00, 0x01 } },
            { KeyFunction.Number4, new byte[4] { 0x21, 0x00, 0x00, 0x01 } },
            { KeyFunction.Number5, new byte[4] { 0x22, 0x00, 0x00, 0x01 } },
            { KeyFunction.Number6, new byte[4] { 0x23, 0x00, 0x00, 0x01 } },
            { KeyFunction.Number7, new byte[4] { 0x24, 0x00, 0x00, 0x01 } },
            { KeyFunction.Number8, new byte[4] { 0x25, 0x00, 0x00, 0x01 } },
            { KeyFunction.Number9, new byte[4] { 0x26, 0x00, 0x00, 0x01 } },
            { KeyFunction.Number0, new byte[4] { 0x27, 0x00, 0x00, 0x01 } },
            { KeyFunction.Return, new byte[4] { 0x28, 0x00, 0x00, 0x01 } },
            { KeyFunction.Escape, new byte[4] { 0x29, 0x00, 0x00, 0x01 } },
            { KeyFunction.Backspace, new byte[4] { 0x2A, 0x00, 0x00, 0x01 } },
            { KeyFunction.Tab, new byte[4] { 0x2B, 0x00, 0x00, 0x01 } },
            { KeyFunction.Space, new byte[4] { 0x2C, 0x00, 0x00, 0x01 } },
            { KeyFunction.Minus, new byte[4] { 0x2D, 0x00, 0x00, 0x01 } },
            { KeyFunction.Equals, new byte[4] { 0x2E, 0x00, 0x00, 0x01 } },

            { KeyFunction.LeftBracket, new byte[4] { 0x2F, 0x00, 0x00, 0x01 } },
            { KeyFunction.RightBracket, new byte[4] { 0x30, 0x00, 0x00, 0x01 } },
            { KeyFunction.Backslash, new byte[4] { 0x31, 0x00, 0x00, 0x01 } },
            { KeyFunction.NonUsPound, new byte[4] { 0x32, 0x00, 0x00, 0x01 } },
            { KeyFunction.Semicolon, new byte[4] { 0x33, 0x00, 0x00, 0x01 } },
            { KeyFunction.Quote, new byte[4] { 0x34, 0x00, 0x00, 0x01 } },
            { KeyFunction.Backtick, new byte[4] { 0x35, 0x00, 0x00, 0x01 } },
            { KeyFunction.Comma, new byte[4] { 0x36, 0x00, 0x00, 0x01 } },
            { KeyFunction.Period, new byte[4] { 0x37, 0x00, 0x00, 0x01 } },
            { KeyFunction.Slash, new byte[4] { 0x38, 0x00, 0x00, 0x01 } },
            { KeyFunction.CapsLock, new byte[4] { 0x39, 0x00, 0x00, 0x01 } },

            { KeyFunction.F1, new byte[4] { 0x3A, 0x00, 0x00, 0x01 } },
            { KeyFunction.F2, new byte[4] { 0x3B, 0x00, 0x00, 0x01 } },
            { KeyFunction.F3, new byte[4] { 0x3C, 0x00, 0x00, 0x01 } },
            { KeyFunction.F4, new byte[4] { 0x3D, 0x00, 0x00, 0x01 } },
            { KeyFunction.F5, new byte[4] { 0x3E, 0x00, 0x00, 0x01 } },
            { KeyFunction.F6, new byte[4] { 0x3F, 0x00, 0x00, 0x01 } },
            { KeyFunction.F7, new byte[4] { 0x40, 0x00, 0x00, 0x01 } },
            { KeyFunction.F8, new byte[4] { 0x41, 0x00, 0x00, 0x01 } },
            { KeyFunction.F9, new byte[4] { 0x42, 0x00, 0x00, 0x01 } },
            { KeyFunction.F10, new byte[4] { 0x43, 0x00, 0x00, 0x01 } },
            { KeyFunction.F11, new byte[4] { 0x44, 0x00, 0x00, 0x01 } },
            { KeyFunction.F12, new byte[4] { 0x45, 0x00, 0x00, 0x01 } },

            { KeyFunction.PrintScreen, new byte[4] { 0x46, 0x00, 0x00, 0x01 } },
            { KeyFunction.ScrollLock, new byte[4] { 0x47, 0x00, 0x00, 0x01 } },
            { KeyFunction.Pause, new byte[4] { 0x48, 0x00, 0x00, 0x01 } },
            { KeyFunction.Insert, new byte[4] { 0x49, 0x00, 0x00, 0x01 } },
            { KeyFunction.Home, new byte[4] { 0x4A, 0x00, 0x00, 0x01 } },
            { KeyFunction.PageUp, new byte[4] { 0x4B, 0x00, 0x00, 0x01 } },
            { KeyFunction.Delete, new byte[4] { 0x4C, 0x00, 0x00, 0x01 } },
            { KeyFunction.End, new byte[4] { 0x4D, 0x00, 0x00, 0x01 } },
            { KeyFunction.PageDown, new byte[4] { 0x4E, 0x00, 0x00, 0x01 } },

            { KeyFunction.RightArrow, new byte[4] { 0x4F, 0x00, 0x00, 0x01 } },
            { KeyFunction.LeftArrow, new byte[4] { 0x50, 0x00, 0x00, 0x01 } },
            { KeyFunction.DownArrow, new byte[4] { 0x51, 0x00, 0x00, 0x01 } },
            { KeyFunction.UpArrow, new byte[4] { 0x52, 0x00, 0x00, 0x01 } },

            { KeyFunction.NumLock, new byte[4] { 0x53, 0x00, 0x00, 0x01 } },
            { KeyFunction.KeypadSlash, new byte[4] { 0x54, 0x00, 0x00, 0x01 } },
            { KeyFunction.KeypadAsterisk, new byte[4] { 0x55, 0x00, 0x00, 0x01 } },
            { KeyFunction.KeypadMinus, new byte[4] { 0x56, 0x00, 0x00, 0x01 } },
            { KeyFunction.KeypadPlus, new byte[4] { 0x57, 0x00, 0x00, 0x01 } },
            { KeyFunction.KeypadEnter, new byte[4] { 0x58, 0x00, 0x00, 0x01 } },
            { KeyFunction.Keypad1End, new byte[4] { 0x59, 0x00, 0x00, 0x01 } },
            { KeyFunction.Keypad2DownArrow, new byte[4] { 0x5A, 0x00, 0x00, 0x01 } },
            { KeyFunction.Keypad3PageDown, new byte[4] { 0x5B, 0x00, 0x00, 0x01 } },
            { KeyFunction.Keypad4LeftArrow, new byte[4] { 0x5C, 0x00, 0x00, 0x01 } },
            { KeyFunction.Keypad5, new byte[4] { 0x5D, 0x00, 0x00, 0x01 } },
            { KeyFunction.Keypad6RightArrow, new byte[4] { 0x5E, 0x00, 0x00, 0x01 } },
            { KeyFunction.Keypad7Home, new byte[4] { 0x5F, 0x00, 0x00, 0x01 } },
            { KeyFunction.Keypad8UpArrow, new byte[4] { 0x60, 0x00, 0x00, 0x01 } },
            { KeyFunction.Keypad9PageUp, new byte[4] { 0x61, 0x00, 0x00, 0x01 } },
            { KeyFunction.Keypad0Insert, new byte[4] { 0x62, 0x00, 0x00, 0x01 } },
            { KeyFunction.KeypadPeriodDelete, new byte[4] { 0x63, 0x00, 0x00, 0x01 } },
            { KeyFunction.NonUsBackslash, new byte[4] { 0x64, 0x00, 0x00, 0x01 } },
            { KeyFunction.Application, new byte[4] { 0x65, 0x00, 0x00, 0x01 } },
            { KeyFunction.KeyboardPower, new byte[4] { 0x66, 0x00, 0x00, 0x01 } },
            { KeyFunction.KeypadEqual, new byte[4] { 0x67, 0x00, 0x00, 0x01 } },

            { KeyFunction.F13, new byte[4] { 0x68, 0x00, 0x00, 0x01 } },
            { KeyFunction.F14, new byte[4] { 0x69, 0x00, 0x00, 0x01 } },
            { KeyFunction.F15, new byte[4] { 0x6A, 0x00, 0x00, 0x01 } },
            { KeyFunction.F16, new byte[4] { 0x6B, 0x00, 0x00, 0x01 } },
            { KeyFunction.F17, new byte[4] { 0x6C, 0x00, 0x00, 0x01 } },
            { KeyFunction.F18, new byte[4] { 0x6D, 0x00, 0x00, 0x01 } },
            { KeyFunction.F19, new byte[4] { 0x6E, 0x00, 0x00, 0x01 } },
            { KeyFunction.F20, new byte[4] { 0x6F, 0x00, 0x00, 0x01 } },
            { KeyFunction.F21, new byte[4] { 0x70, 0x00, 0x00, 0x01 } },
            { KeyFunction.F22, new byte[4] { 0x71, 0x00, 0x00, 0x01 } },
            { KeyFunction.F23, new byte[4] { 0x72, 0x00, 0x00, 0x01 } },
            { KeyFunction.F24, new byte[4] { 0x73, 0x00, 0x00, 0x01 } },

            { KeyFunction.KeypadComma, new byte[4] { 0x85, 0x00, 0x00, 0x01 } },
            { KeyFunction.International1, new byte[4] { 0x87, 0x00, 0x00, 0x01 } },
            { KeyFunction.International2, new byte[4] { 0x88, 0x00, 0x00, 0x01 } },
            { KeyFunction.International3, new byte[4] { 0x89, 0x00, 0x00, 0x01 } },
            { KeyFunction.International4, new byte[4] { 0x8A, 0x00, 0x00, 0x01 } },
            { KeyFunction.International5, new byte[4] { 0x8B, 0x00, 0x00, 0x01 } },
            { KeyFunction.Lang1, new byte[4] { 0x90, 0x00, 0x00, 0x01 } },

            { KeyFunction.LeftControl, new byte[4] { 0xE0, 0x00, 0x00, 0x01 } },
            { KeyFunction.LeftShift, new byte[4] { 0xE1, 0x00, 0x00, 0x01 } },
            { KeyFunction.LeftAlt, new byte[4] { 0xE2, 0x00, 0x00, 0x01 } },
            { KeyFunction.LeftGUI, new byte[4] { 0xE3, 0x00, 0x00, 0x01 } },
            { KeyFunction.RightControl, new byte[4] { 0xE4, 0x00, 0x00, 0x01 } },
            { KeyFunction.RightShift, new byte[4] { 0xE5, 0x00, 0x00, 0x01 } },
            { KeyFunction.RightAlt, new byte[4] { 0xE6, 0x00, 0x00, 0x01 } },
            { KeyFunction.RightGUI, new byte[4] { 0xE7, 0x00, 0x00, 0x01 } },

            { KeyFunction.Europe1, new byte[4] { 0x32, 0x00, 0x00, 0x01 } },
            { KeyFunction.Europe2, new byte[4] { 0x64, 0x00, 0x00, 0x01 } },
            #endregion

            #region MouseKey
            { KeyFunction.MouseLButton, new byte[4] { 0xF0, 0x00, 0x00, 0x02 } },
            { KeyFunction.MouseRButton, new byte[4] { 0xF1, 0x00, 0x00, 0x02 } },
            { KeyFunction.MouseMButton, new byte[4] { 0xF2, 0x00, 0x00, 0x02 } },
            { KeyFunction.MouseB4Button, new byte[4] { 0xF3, 0x00, 0x00, 0x02 } },
            { KeyFunction.MouseB5Button, new byte[4] { 0xF4, 0x00, 0x00, 0x02 } },
            #endregion

            #region MediaKey
            { KeyFunction.FastForward,          new byte[4] { 0xB3, 0x00, 0x00, 0x03 } },
            { KeyFunction.Rewind,               new byte[4] { 0xB4, 0x00, 0x00, 0x03 } },
            { KeyFunction.ScanNextTrack,        new byte[4] { 0xB5, 0x00, 0x00, 0x03 } },
            { KeyFunction.ScanPreviousTrack,    new byte[4] { 0xB6, 0x00, 0x00, 0x03 } },
            { KeyFunction.Stop,                 new byte[4] { 0xB7, 0x00, 0x00, 0x03 } },
            { KeyFunction.PlayAndPause,         new byte[4] { 0xCD, 0x00, 0x00, 0x03 } },
            { KeyFunction.Mute,                 new byte[4] { 0xE2, 0x00, 0x00, 0x03 } },
            { KeyFunction.VolumeUp,             new byte[4] { 0xE9, 0x00, 0x00, 0x03 } },
            { KeyFunction.VolumeDown,           new byte[4] { 0xEA, 0x00, 0x00, 0x03 } },

            { KeyFunction.MediaSelect,  new byte[4] { 0x83, 0x01, 0x00, 0x03 } },
            { KeyFunction.Mail,         new byte[4] { 0x8A, 0x01, 0x00, 0x03 } },
            { KeyFunction.Calculator,   new byte[4] { 0x92, 0x01, 0x00, 0x03 } },
            { KeyFunction.MyComputer,   new byte[4] { 0x94, 0x01, 0x00, 0x03 } },

            { KeyFunction.WebSearch,    new byte[4] { 0x21, 0x02, 0x00, 0x03 } },
            { KeyFunction.WebHome,      new byte[4] { 0x23, 0x02, 0x00, 0x03 } },
            { KeyFunction.WebBack,      new byte[4] { 0x24, 0x02, 0x00, 0x03 } },
            { KeyFunction.WebForward,   new byte[4] { 0x25, 0x02, 0x00, 0x03 } },
            { KeyFunction.WebStop,      new byte[4] { 0x26, 0x02, 0x00, 0x03 } },
            { KeyFunction.WebRefresh,   new byte[4] { 0x27, 0x02, 0x00, 0x03 } },
            { KeyFunction.WebFavorite,  new byte[4] { 0x2A, 0x02, 0x00, 0x03 } },
            #endregion

            #region SystemKey
            { KeyFunction.Power, new byte[4] { 0x01, 0x00, 0x00, 0x04 } },
            { KeyFunction.Sleep, new byte[4] { 0x02, 0x00, 0x00, 0x04 } },
            { KeyFunction.Wake,  new byte[4] { 0x04, 0x00, 0x00, 0x04 } },
            #endregion

            #region ProfileKey
            { KeyFunction.ProfilePlus,      new byte[4] { 0x04, 0x01, 0x01, 0xF0 } },
            { KeyFunction.ProfileMinus,     new byte[4] { 0x04, 0x00, 0x01, 0xF0 } },
            { KeyFunction.ProfilePlusLoop,  new byte[4] { 0x03, 0x00, 0x01, 0xF0 } },
            #endregion

            #region RGBKey
            { KeyFunction.RGBOnOff, new byte[4] { 0xFF, 0x00, 0x00, 0x0A } },
            { KeyFunction.RGBEffectLoop, new byte[4] { 0x03, 0x00, 0x00, 0x0A } },

            { KeyFunction.BrightnessIncrease, new byte[4] { 0x01, 0x00, 0x01, 0x0A } },
            { KeyFunction.BrightnessDecrease, new byte[4] { 0x02, 0x00, 0x01, 0x0A } },

            { KeyFunction.SpeedIncrease, new byte[4] { 0x01, 0x00, 0x02, 0x0A } },
            { KeyFunction.SpeedDecrease, new byte[4] { 0x02, 0x00, 0x02, 0x0A } },
            { KeyFunction.SpeedLoop, new byte[4] { 0x03, 0x00, 0x02, 0x0A } },

            { KeyFunction.ColorIncrease, new byte[4] { 0x01, 0x00, 0x03, 0x0A } },
            { KeyFunction.ColorDecrease, new byte[4] { 0x02, 0x00, 0x03, 0x0A } },
            { KeyFunction.ColorLoop, new byte[4] { 0x03, 0x00, 0x03, 0x0A } },

            { KeyFunction.DirectionLoop, new byte[4] { 0x03, 0x00, 0x04, 0x0A } },
            #endregion

            #region PassThrough
            { KeyFunction.PassThrough, new byte[4] { 0x00, 0x00, 0x00, 0xF8 } },
            #endregion
            { KeyFunction.None, new byte[4] { 0x00, 0x00, 0x00, 0x00} }
        };

        /// <summary>
        /// 3 input, all of the inputs are standard key, byte 0, 1, 2
        /// </summary>
        public static readonly Dictionary<KeyFunction, byte[]> COMBINATION_KEY_BYTES = new()
        {
            /*#01*/
            { KeyFunction.CombinationKey, new byte[4] { 0x00, 0x00, 0x00, 0x01} }
        };

        /// <summary>
        /// 1 input, byte 1
        /// </summary>
        public static readonly Dictionary<KeyFunction, byte[]> MOUSE_LAYER_PROFILE_RGB_KEY_BYTES = new()
        {
            #region LayerKey #02
            { KeyFunction.MouseWheelUp, new byte[4] { 0xF5, 0x00, 0x00, 0x02 } },
            { KeyFunction.MouseWheelDown, new byte[4] { 0xF6, 0x00, 0x00, 0x02 } },
            { KeyFunction.MouseACPanLeft, new byte[4] { 0xF7, 0x00, 0x00, 0x02 } },
            { KeyFunction.MouseACPanRight, new byte[4] { 0xF8, 0x00, 0x00, 0x02 } },
            { KeyFunction.MouseXPanLeft, new byte[4] { 0xF9, 0x00, 0x00, 0x02 } },
            { KeyFunction.MouseXPanRight, new byte[4] { 0xFA, 0x00, 0x00, 0x02 } },
            { KeyFunction.MouseXPanUp, new byte[4] { 0xFB, 0x00, 0x00, 0x02 } },
            { KeyFunction.MouseXPanDown, new byte[4] { 0xFC, 0x00, 0x00, 0x02 } },
            #endregion

            #region LayerKey #F0
            { KeyFunction.MOSwitch, new byte[4] { 0x15, 0x00, 0x00, 0xF0 } },
            { KeyFunction.TGSwitch, new byte[4] { 0x16, 0x00, 0x00, 0xF0 } },
            { KeyFunction.TOSwitch, new byte[4] { 0x17, 0x00, 0x00, 0xF0 } },
            { KeyFunction.DFSwitch, new byte[4] { 0x18, 0x00, 0x00, 0xF0 } },
            #endregion

            #region ProfileKey #F0
            { KeyFunction.ProfileValue, new byte[4] { 0x04, 0x00, 0x01, 0xF0 } },
            #endregion

            /* #0A */
            { KeyFunction.RGBEffectValue, new byte[4] { 0x04, 0x00, 0x00, 0x0A } },
            { KeyFunction.DirectionValue, new byte[4] { 0x04, 0x00, 0x04, 0x0A } },
        };

        /// <summary>
        /// 1 input, byte 2
        /// </summary>
        public static readonly Dictionary<KeyFunction, byte[]> PROFILE1_MACRO_KEY_BYTES = new()
        {
            /*#05*/
            { KeyFunction.Macro1, new byte[4] { 0x00, 0x00, 0x00, 0x05} },
            { KeyFunction.Macro2, new byte[4] { 0x01, 0x00, 0x00, 0x05} },
            { KeyFunction.Macro3, new byte[4] { 0x02, 0x00, 0x00, 0x05} },
            { KeyFunction.Macro4, new byte[4] { 0x03, 0x00, 0x00, 0x05} },
            { KeyFunction.Macro5, new byte[4] { 0x04, 0x00, 0x00, 0x05} },
            { KeyFunction.Macro6, new byte[4] { 0x05, 0x00, 0x00, 0x05} },
            { KeyFunction.Macro7, new byte[4] { 0x06, 0x00, 0x00, 0x05} },
            { KeyFunction.Macro8, new byte[4] { 0x07, 0x00, 0x00, 0x05} },
            { KeyFunction.Macro9, new byte[4] { 0x08, 0x00, 0x00, 0x05} },
            { KeyFunction.Macro10, new byte[4] { 0x09, 0x00, 0x00, 0x05} },
            { KeyFunction.Macro11, new byte[4] { 0x0A, 0x00, 0x00, 0x05} },
            { KeyFunction.Macro12, new byte[4] { 0x0B, 0x00, 0x00, 0x05} },
            { KeyFunction.Macro13, new byte[4] { 0x0C, 0x00, 0x00, 0x05} },
            { KeyFunction.Macro14, new byte[4] { 0x0D, 0x00, 0x00, 0x05} },
            { KeyFunction.Macro15, new byte[4] { 0x0E, 0x00, 0x00, 0x05} },
            { KeyFunction.Macro16, new byte[4] { 0x0F, 0x00, 0x00, 0x05} },
        };

        public static readonly Dictionary<KeyFunction, byte[]> PROFILE2_MACRO_KEY_BYTES = new()
        {
            /*#05*/
            { KeyFunction.Macro1, new byte[4] { 0x10, 0x00, 0x00, 0x05} },
            { KeyFunction.Macro2, new byte[4] { 0x11, 0x00, 0x00, 0x05} },
            { KeyFunction.Macro3, new byte[4] { 0x12, 0x00, 0x00, 0x05} },
            { KeyFunction.Macro4, new byte[4] { 0x13, 0x00, 0x00, 0x05} },
            { KeyFunction.Macro5, new byte[4] { 0x14, 0x00, 0x00, 0x05} },
            { KeyFunction.Macro6, new byte[4] { 0x15, 0x00, 0x00, 0x05} },
            { KeyFunction.Macro7, new byte[4] { 0x16, 0x00, 0x00, 0x05} },
            { KeyFunction.Macro8, new byte[4] { 0x17, 0x00, 0x00, 0x05} },
            { KeyFunction.Macro9, new byte[4] { 0x18, 0x00, 0x00, 0x05} },
            { KeyFunction.Macro10, new byte[4] { 0x19, 0x00, 0x00, 0x05} },
            { KeyFunction.Macro11, new byte[4] { 0x1A, 0x00, 0x00, 0x05} },
            { KeyFunction.Macro12, new byte[4] { 0x1B, 0x00, 0x00, 0x05} },
            { KeyFunction.Macro13, new byte[4] { 0x1C, 0x00, 0x00, 0x05} },
            { KeyFunction.Macro14, new byte[4] { 0x1D, 0x00, 0x00, 0x05} },
            { KeyFunction.Macro15, new byte[4] { 0x1E, 0x00, 0x00, 0x05} },
            { KeyFunction.Macro16, new byte[4] { 0x1F, 0x00, 0x00, 0x05} },
        };

        /// <summary>
        /// 1 input, byte 0
        /// </summary>
        public static readonly Dictionary<KeyFunction, byte[]> SOFTWARE_KEY_BYTES = new()
        {
            /* #F1 */
            { KeyFunction.SoftwareControl, new byte[4] { 0x00, 0x00, 0x00, 0xF1 } },
        };

        // NOTE: Legacy KEY_FUNCTION_INPUT_TYPE entries referenced StandardKey, FwAnimationMode, and
        // FwAnimationDirection types that are not yet ported into qos-service. Those Type[] slots
        // are substituted with typeof(int) so the metadata table compiles. The byte tables above
        // (the firmware contract) are unaffected. See port notes in the spec.
        public static readonly Dictionary<KeyFunction, KeyFunctionModel> KEY_FUNCTION_INPUT_TYPE = new()
        {
            /*#01*/
            { KeyFunction.CombinationKey, new KeyFunctionModel(KeyFunction.CombinationKey, new Type[3] { typeof(int), typeof(int), typeof(int) }, new int[3] { 0, 1, 2 }, new string[3] { "key 1", "key 2", "key 3" }) },
            #region LayerKey #02
            { KeyFunction.MouseWheelUp, new KeyFunctionModel(KeyFunction.MouseWheelUp, new Type[1] { typeof(int) }, new int[1] { 1 }, new string[1] { "scroll wheel index" }) },
            { KeyFunction.MouseWheelDown, new KeyFunctionModel(KeyFunction.MouseWheelDown, new Type[1] { typeof(int) }, new int[1] { 1 }, new string[1] { "scroll wheel index" }) },
            { KeyFunction.MouseACPanLeft, new KeyFunctionModel(KeyFunction.MouseACPanLeft, new Type[1] { typeof(int) }, new int[1] { 1 }, new string[1] { "scroll wheel index" }) },
            { KeyFunction.MouseACPanRight, new KeyFunctionModel(KeyFunction.MouseACPanRight, new Type[1] { typeof(int) }, new int[1] { 1 }, new string[1] { "scroll wheel index" }) },
            { KeyFunction.MouseXPanLeft, new KeyFunctionModel(KeyFunction.MouseXPanLeft, new Type[1] { typeof(int) }, new int[1] { 1 }, new string[1] { "mouse move index" }) },
            { KeyFunction.MouseXPanRight, new KeyFunctionModel(KeyFunction.MouseXPanRight, new Type[1] { typeof(int) }, new int[1] { 1 }, new string[1] { "mouse move index" }) },
            { KeyFunction.MouseXPanUp, new KeyFunctionModel(KeyFunction.MouseXPanUp, new Type[1] { typeof(int) }, new int[1] { 1 }, new string[1] { "mouse move index" }) },
            { KeyFunction.MouseXPanDown, new KeyFunctionModel(KeyFunction.MouseXPanDown, new Type[1] { typeof(int) }, new int[1] { 1 }, new string[1] { "mouse move index" }) },
            #endregion

            #region LayerKey #F0
            { KeyFunction.MOSwitch, new KeyFunctionModel(KeyFunction.MOSwitch, new Type[1] { typeof(int) }, new int[1] { 1 }, new string[1] { "layer" }) },
            { KeyFunction.TGSwitch, new KeyFunctionModel(KeyFunction.TGSwitch, new Type[1] { typeof(int) }, new int[1] { 1 }, new string[1] { "layer" }) },
            { KeyFunction.TOSwitch, new KeyFunctionModel(KeyFunction.TOSwitch, new Type[1] { typeof(int) }, new int[1] { 1 }, new string[1] { "layer" }) },
            { KeyFunction.DFSwitch, new KeyFunctionModel(KeyFunction.DFSwitch, new Type[1] { typeof(int) }, new int[1] { 1 }, new string[1] { "layer" }) },
            #endregion

            #region ProfileKey #F0
            { KeyFunction.ProfileValue, new KeyFunctionModel(KeyFunction.ProfileValue, new Type[1] { typeof(int) }, new int[1] { 1 }, new string[1] { "profile" }) },
            #endregion

            /* #0A */
            { KeyFunction.RGBEffectValue, new KeyFunctionModel(KeyFunction.RGBEffectValue, new Type[1] { typeof(int) }, new int[1] { 1 }, new string[1] { "type" }) },
            { KeyFunction.DirectionValue, new KeyFunctionModel(KeyFunction.DirectionValue, new Type[1] { typeof(int) }, new int[1] { 1 }, new string[1] { "direction" }) },

            /*#05*/
            { KeyFunction.Macro1, new KeyFunctionModel(KeyFunction.Macro1, new Type[1] { typeof(MacroRepeatMode) }, new int[1] { 2 }, new string[1] { "repeat mode" }) },
            { KeyFunction.Macro2, new KeyFunctionModel(KeyFunction.Macro1, new Type[1] { typeof(MacroRepeatMode) }, new int[1] { 2 }, new string[1] { "repeat mode" }) },
            { KeyFunction.Macro3, new KeyFunctionModel(KeyFunction.Macro1, new Type[1] { typeof(MacroRepeatMode) }, new int[1] { 2 }, new string[1] { "repeat mode" }) },
            { KeyFunction.Macro4, new KeyFunctionModel(KeyFunction.Macro1, new Type[1] { typeof(MacroRepeatMode) }, new int[1] { 2 }, new string[1] { "repeat mode" }) },
            { KeyFunction.Macro5, new KeyFunctionModel(KeyFunction.Macro1, new Type[1] { typeof(MacroRepeatMode) }, new int[1] { 2 }, new string[1] { "repeat mode" }) },
            { KeyFunction.Macro6, new KeyFunctionModel(KeyFunction.Macro1, new Type[1] { typeof(MacroRepeatMode) }, new int[1] { 2 }, new string[1] { "repeat mode" }) },
            { KeyFunction.Macro7, new KeyFunctionModel(KeyFunction.Macro1, new Type[1] { typeof(MacroRepeatMode) }, new int[1] { 2 }, new string[1] { "repeat mode" }) },
            { KeyFunction.Macro8, new KeyFunctionModel(KeyFunction.Macro1, new Type[1] { typeof(MacroRepeatMode) }, new int[1] { 2 }, new string[1] { "repeat mode" }) },
            { KeyFunction.Macro9, new KeyFunctionModel(KeyFunction.Macro1, new Type[1] { typeof(MacroRepeatMode) }, new int[1] { 2 }, new string[1] { "repeat mode" }) },
            { KeyFunction.Macro10, new KeyFunctionModel(KeyFunction.Macro1, new Type[1] { typeof(MacroRepeatMode) }, new int[1] { 2 }, new string[1] { "repeat mode" }) },
            { KeyFunction.Macro11, new KeyFunctionModel(KeyFunction.Macro1, new Type[1] { typeof(MacroRepeatMode) }, new int[1] { 2 }, new string[1] { "repeat mode" }) },
            { KeyFunction.Macro12, new KeyFunctionModel(KeyFunction.Macro1, new Type[1] { typeof(MacroRepeatMode) }, new int[1] { 2 }, new string[1] { "repeat mode" }) },
            { KeyFunction.Macro13, new KeyFunctionModel(KeyFunction.Macro1, new Type[1] { typeof(MacroRepeatMode) }, new int[1] { 2 }, new string[1] { "repeat mode" }) },
            { KeyFunction.Macro14, new KeyFunctionModel(KeyFunction.Macro1, new Type[1] { typeof(MacroRepeatMode) }, new int[1] { 2 }, new string[1] { "repeat mode" }) },
            { KeyFunction.Macro15, new KeyFunctionModel(KeyFunction.Macro1, new Type[1] { typeof(MacroRepeatMode) }, new int[1] { 2 }, new string[1] { "repeat mode" }) },
            { KeyFunction.Macro16, new KeyFunctionModel(KeyFunction.Macro1, new Type[1] { typeof(MacroRepeatMode) }, new int[1] { 2 }, new string[1] { "repeat mode" }) },

            /* #F1 */
            { KeyFunction.SoftwareControl, new KeyFunctionModel(KeyFunction.SoftwareControl, new Type[1] { typeof(int) }, new int[1] { 0 }, new string[1] { "software flag" }) },
        };

        /// <summary>
        /// List of RollerFunction byte dictionary vs VariantByte array
        /// </summary>
        public static Dictionary<Dictionary<KeyFunction, byte[]>, VariantByte[]> PROFILE1_FUNCTION_KEY_CHANGING_BYTE_INDEX = new()
        {
            { STATIC_KEY_BYTES, new VariantByte[4] { new(false), new(false), new(false), new(false) } },
            { MOUSE_LAYER_PROFILE_RGB_KEY_BYTES, new VariantByte[4] { new(false), new(true), new(false), new(false) } },
            { PROFILE1_MACRO_KEY_BYTES, new VariantByte[4] { new(false), new(false), new(true), new(false) } },
            { SOFTWARE_KEY_BYTES, new VariantByte[4] { new(true), new(false), new(false), new(false) } },
            { COMBINATION_KEY_BYTES, new VariantByte[4] { new(true), new(true), new(true), new(false) } },
        };

        public static Dictionary<Dictionary<KeyFunction, byte[]>, VariantByte[]> PROFILE2_FUNCTION_KEY_CHANGING_BYTE_INDEX = new()
        {
            { STATIC_KEY_BYTES, new VariantByte[4] { new(false), new(false), new(false), new(false) } },
            { MOUSE_LAYER_PROFILE_RGB_KEY_BYTES, new VariantByte[4] { new(false), new(true), new(false), new(false) } },
            { PROFILE2_MACRO_KEY_BYTES, new VariantByte[4] { new(false), new(false), new(true), new(false) } },
            { SOFTWARE_KEY_BYTES, new VariantByte[4] { new(true), new(false), new(false), new(false) } },
            { COMBINATION_KEY_BYTES, new VariantByte[4] { new(true), new(true), new(true), new(false) } },
        };

        public static Dictionary<KeebProfile, Dictionary<Dictionary<KeyFunction, byte[]>, VariantByte[]>> PROFILE_FUNCTION_BYTE = new()
        {
            {KeebProfile.Profile1, PROFILE1_FUNCTION_KEY_CHANGING_BYTE_INDEX},
            {KeebProfile.Profile2, PROFILE2_FUNCTION_KEY_CHANGING_BYTE_INDEX},
        };

        public static Dictionary<Dictionary<KeyFunction, byte[]>, VariantByte[]> ALL_PROFILE_FUNCTION_KEY_CHANGING_BYTE_INDEX = new()
        {
            { STATIC_KEY_BYTES, new VariantByte[4] { new(false), new(false), new(false), new(false) } },
            { MOUSE_LAYER_PROFILE_RGB_KEY_BYTES, new VariantByte[4] { new(false), new(true), new(false), new(false) } },
            { PROFILE1_MACRO_KEY_BYTES, new VariantByte[4] { new(false), new(false), new(true), new(false) } },
            { PROFILE2_MACRO_KEY_BYTES, new VariantByte[4] { new(false), new(false), new(true), new(false) } },
            { SOFTWARE_KEY_BYTES, new VariantByte[4] { new(true), new(false), new(false), new(false) } },
            { COMBINATION_KEY_BYTES, new VariantByte[4] { new(true), new(true), new(true), new(false) } },
        };
    }
}
