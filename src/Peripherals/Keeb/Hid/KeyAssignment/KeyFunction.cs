namespace Qos.Service.Peripherals.Keeb.Hid.KeyAssignment
{
    public enum KeyFunction
    {
        #region StandardKey
        None,
        A, B, C, D, E, F, G, H, I, J, K, L, M, N, O, P, Q, R, S, T, U, V, W, X, Y, Z,
        Number1, Number2, Number3, Number4, Number5, Number6, Number7, Number8, Number9, Number0,
        Return, Escape, Backspace, Tab, Space, Minus, Equals,
        LeftBracket, RightBracket, Backslash, NonUsPound, Semicolon, Quote, Backtick, Comma, Period, Slash, CapsLock,
        F1, F2, F3, F4, F5, F6, F7, F8, F9, F10, F11, F12,
        PrintScreen, ScrollLock, Pause,
        Insert, Home, PageUp,
        Delete, End, PageDown,
        RightArrow, LeftArrow, DownArrow, UpArrow,
        NumLock, KeypadSlash, KeypadAsterisk, KeypadMinus, KeypadPlus, KeypadEnter,
        Keypad1End, Keypad2DownArrow, Keypad3PageDown,
        Keypad4LeftArrow, Keypad5, Keypad6RightArrow,
        Keypad7Home, Keypad8UpArrow, Keypad9PageUp, Keypad0Insert,
        KeypadPeriodDelete, NonUsBackslash, Application, KeyboardPower, KeypadEqual,
        F13, F14, F15, F16, F17, F18, F19, F20, F21, F22, F23, F24,
        KeypadComma,
        International1,
        International2,
        International3,
        International4,
        International5,
        Lang1,
        LeftControl, LeftShift, LeftAlt, LeftGUI,
        RightControl, RightShift, RightAlt, RightGUI,
        CombinationKey,
        Europe1, Europe2,
        #endregion

        #region MouseKey
        MouseLButton,
        MouseRButton,
        MouseMButton,
        MouseB4Button,
        MouseB5Button,
        MouseWheelUp,
        MouseWheelDown,
        MouseACPanLeft,
        MouseACPanRight,
        MouseXPanLeft,
        MouseXPanRight,
        MouseXPanUp,
        MouseXPanDown,
        #endregion

        #region MediaKey
        FastForward,
        Rewind,
        ScanNextTrack,
        ScanPreviousTrack,
        Stop,
        PlayAndPause,
        Mute,
        VolumeUp,
        VolumeDown,

        MediaSelect,
        Mail,
        Calculator,
        MyComputer,

        WebSearch,
        WebHome,
        WebBack,
        WebForward,
        WebStop,
        WebRefresh,
        WebFavorite,
        #endregion

        #region MacroKey
        Macro1,
        Macro2,
        Macro3,
        Macro4,
        Macro5,
        Macro6,
        Macro7,
        Macro8,
        Macro9,
        Macro10,
        Macro11,
        Macro12,
        Macro13,
        Macro14,
        Macro15,
        Macro16,
        #endregion

        #region SystemKey
        Power,
        Sleep,
        Wake,
        #endregion

        #region LayerKey
        MOSwitch,
        TGSwitch,
        TOSwitch,
        DFSwitch,
        #endregion

        #region ProfileKey
        ProfilePlus,
        ProfileMinus,
        ProfilePlusLoop,
        ProfileValue,
        #endregion

        #region RGBKey
        RGBOnOff,
        RGBEffectLoop,
        RGBEffectValue,

        BrightnessIncrease,
        BrightnessDecrease,

        SpeedIncrease,
        SpeedDecrease,
        SpeedLoop,

        ColorIncrease,
        ColorDecrease,
        ColorLoop,

        DirectionLoop,
        DirectionValue,
        #endregion

        #region PassThrough
        PassThrough,
        #endregion

        #region SoftwareControl
        SoftwareControl,
        #endregion
    }
}
