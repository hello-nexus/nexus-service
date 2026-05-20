using Qos.Service.Peripherals.Keeb.Hid.Enums;

namespace Qos.Service.Peripherals.Keeb.Hid.Settings
{
    // FLAG: legacy ColorRGB had richer HSL/brightness helpers (LightDancing.Colors.ColorRGB).
    // qos-service currently has Qos.Service.Lighting.Rgb.RgbColor (R/G/B only, no JSON shape)
    // and Qos.Service.Models.Common.RGBA (R/G/B byte + double A, no positional ctor).
    // Neither matches the legacy 3-byte positional constructor used by these settings,
    // so we keep a lean local ColorRGB readonly struct to preserve the byte layout used
    // by KeebSettings.GetCommands / GetSettingFromCommands.
    public readonly struct ColorRGB
    {
        public ColorRGB(byte r, byte g, byte b) { R = r; G = g; B = b; }

        public byte R { get; }
        public byte G { get; }
        public byte B { get; }
    }

    public class KeebFwAnimationSetting
    {
        public FwAnimationMode AnimationMode { private set; get; } = FwAnimationMode.Wave;

        /// <summary>
        /// Speed Range from 1 ~ 5 (1 is the fast)
        /// </summary>
        public FwAnimationSpeed LedSpeed { private set; get; } = FwAnimationSpeed.Standard;
        public SelectedColor SelectedColor { private set; get; } = SelectedColor.MultipleColors;
        public FwAnimationDirection LedDirection { private set; get; } = FwAnimationDirection.LeftToRight;

        public ColorRGB[] LedColors { private set; get; } = new ColorRGB[8]
        {
            new(255, 0, 0),
            new(255, 125, 0),
            new(125, 255, 0),
            new(0, 255, 0),
            new(0, 255, 125),
            new(0, 125, 255),
            new(0, 0, 255),
            new(125, 0, 255),
        };

        public KeebFwAnimationSetting() { }

        public KeebFwAnimationSetting(FwAnimationMode fwAnimationMode, FwAnimationSpeed speed, SelectedColor selectedColor, FwAnimationDirection ledDirection, ColorRGB[] ledColors)
        {
            AnimationMode = fwAnimationMode;
            LedSpeed = speed;
            SelectedColor = selectedColor;
            LedDirection = ledDirection;
            LedColors = ledColors;
        }

        public void ChangeFwAnimationMode(FwAnimationMode mode)
        {
            AnimationMode = mode;
            if (mode == FwAnimationMode.Static)
                SelectedColor = SelectedColor.SingleColor1;
            else
                SelectedColor = SelectedColor.MultipleColors;
        }

        /// <summary>
        /// Speed is from 1 ~ 5
        /// </summary>
        /// <param name="ledSpeed"> 1 ~ 5 </param>
        public void ChangeSpeed(FwAnimationSpeed ledSpeed)
        {
            LedSpeed = ledSpeed;
        }

        public void ChangeDirection(FwAnimationDirection ledDirection)
        {
            LedDirection = ledDirection;
        }

        public void ChangeColorSets(ColorRGB[] colors)
        {
            LedColors = colors;
        }

        public void ChangeSelectedColor(SelectedColor selectedColor)
        {
            SelectedColor = selectedColor;
        }
    }
}
