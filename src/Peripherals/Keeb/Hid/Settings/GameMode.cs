namespace Qos.Service.Peripherals.Keeb.Hid.Settings
{
    public class GameMode
    {
        public bool IsWindowsKeyOff { private set; get; } = false;
        public bool IsShiftTabKeyOff { private set; get; } = false;
        public bool IsAltF4KeyOff { private set; get; } = false;
        public bool IsAltTabKeyOff { private set; get; } = false;

        public void SetGameMode(bool? isWindowsKeyOff, bool? isShiftTabKeyOff, bool? isAltF4KeyOff, bool? isAltTabKeyOff)
        {
            IsWindowsKeyOff = isWindowsKeyOff ?? IsWindowsKeyOff;
            IsShiftTabKeyOff = isShiftTabKeyOff ?? IsShiftTabKeyOff;
            IsAltF4KeyOff = isAltF4KeyOff ?? IsAltF4KeyOff;
            // Legacy nexus-control-service had `?? IsAltF4KeyOff` here — a
            // copy-paste bug that quietly tied Alt+Tab's nullable fallback to
            // Alt+F4. Fixed: a null `isAltTabKeyOff` keeps the existing
            // Alt+Tab lockout, not the Alt+F4 one.
            IsAltTabKeyOff = isAltTabKeyOff ?? IsAltTabKeyOff;
        }
    }
}
