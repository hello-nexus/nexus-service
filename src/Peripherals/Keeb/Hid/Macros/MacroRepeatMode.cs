namespace Qos.Service.Peripherals.Keeb.Hid.Macros
{
    public enum MacroRepeatMode
    {
        Normal = 0x01,
        Repeating = 0x02,
        RepeatOnButtonPress = 0x03,
    }

    public enum KeyStatus
    {
        Make = 0,
        Break = 1,
    }
}
