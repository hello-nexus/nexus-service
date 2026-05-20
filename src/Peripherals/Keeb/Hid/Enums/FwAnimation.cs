namespace Qos.Service.Peripherals.Keeb.Hid.Enums
{
    public enum FwAnimationMode
    {
        Static = 0x01,
        Breathe = 0x02,
        Rainbow = 0x03,
        Wave = 0x04,
        Flow = 0x05,
        PingPong = 0x06
    }

    public enum FwAnimationDirection
    {
        LeftToRight = 0x00,
        RightToLeft = 0x01,
        TopToBottom = 0x02,
        BottomToTop = 0x03,
    }
}
