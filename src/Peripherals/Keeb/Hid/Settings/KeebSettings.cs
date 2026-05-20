using Qos.Service.Peripherals.Keeb.Hid.Enums;
using System;
using System.Collections;

namespace Qos.Service.Peripherals.Keeb.Hid.Settings
{
    /// <summary>
    /// Stubbed ScrollWheel container. The legacy implementation
    /// (LightDancing.Hardware.Devices.KeebTKL.Settings.Scrollwheels.ScrollWheel)
    /// pulls in roller-function tables that aren't ported yet. Surface area is the
    /// minimum needed by <see cref="KeebSettings.GetSettingFromCommands"/> and
    /// <see cref="KeebSettings.GetCommands"/>.
    /// </summary>
    public class ScrollWheel
    {
        public byte[] Commands { get; } = new byte[8];

        public void GetScrollModeFromFw(byte[] commands)
        {
            // Stub: real port should decode roller function + key bindings.
            if (commands == null) return;
            int len = Math.Min(commands.Length, Commands.Length);
            Array.Copy(commands, 0, Commands, 0, len);
        }
    }

    public class KeebSettings
    {
        private int _ledBrightnessPercentage = 100;
        public int BtnDebounce { private set; get; } = 1; /*Not using*/
        public GameMode KeebGameMode { private set; get; } = new GameMode();

        /// <summary>
        /// Led Brightness Range from 0 ~ 100 %
        /// </summary>
        public int LedBrightnessPercentage
        {
            private set { _ledBrightnessPercentage = (value >= 0) ? (value <= 100 ? value : 100) : 0; }
            get => _ledBrightnessPercentage;
        }
        public ScrollwheelMode ScrollWheelMode { private set; get; } = ScrollwheelMode.Firmware;
        public ScrollWheel ScrollWheelFunctions { private set; get; } = new ScrollWheel();
        public bool LedOn { private set; get; } = true;
        public KeebFwAnimationSetting CurrentFwAnimationSetting { private set; get; } = new();

        public void GetSettingFromCommands(byte[] commands)
        {
            BtnDebounce = commands[1];
            /*Game Mode*/
            BitArray bits = new(new byte[] { commands[2] });
            KeebGameMode.SetGameMode(bits[0], bits[1], bits[2], bits[3]);
            LedOn = bits[4];

            /*Fw Animation*/
            FwAnimationMode animationMode = (FwAnimationMode)commands[3];
            LedBrightnessPercentage = (int)((double)commands[4] / 255 * 100);
            FwAnimationSpeed ledSpeed = (FwAnimationSpeed)commands[5];
            SelectedColor selectedColor = (SelectedColor)commands[6];
            FwAnimationDirection ledDirection = (FwAnimationDirection)commands[7];
            ColorRGB[] colorSets = new ColorRGB[8];
            for (int i = 0; i < colorSets.Length; i++)
            {
                colorSets[i] = new ColorRGB(commands[12 + i * 3], commands[13 + i * 3], commands[14 + i * 3]);
            }

            CurrentFwAnimationSetting = new KeebFwAnimationSetting(animationMode, ledSpeed, selectedColor, ledDirection, colorSets);

            /*ScrollWheel*/
            ScrollWheelMode = (ScrollwheelMode)commands[36];

            byte[] scrollCommands = new byte[8];
            Array.Copy(commands, 37, scrollCommands, 0, 8);
            ScrollWheelFunctions.GetScrollModeFromFw(scrollCommands);
        }

        public byte[] GetCommands()
        {
            byte[] commands = new byte[65];
            commands[1] = (byte)BtnDebounce;

            //byte2
            BitArray bits = new(8);
            bits[0] = KeebGameMode.IsWindowsKeyOff;
            bits[1] = KeebGameMode.IsShiftTabKeyOff;
            bits[2] = KeebGameMode.IsAltF4KeyOff;
            bits[3] = KeebGameMode.IsAltTabKeyOff;
            bits[4] = LedOn;
            bits.CopyTo(commands, 2);

            commands[3] = (byte)(CurrentFwAnimationSetting.AnimationMode);
            commands[4] = (byte)((double)LedBrightnessPercentage / 100 * 255);
            commands[5] = (byte)CurrentFwAnimationSetting.LedSpeed;
            commands[6] = (byte)CurrentFwAnimationSetting.SelectedColor;
            commands[7] = (byte)CurrentFwAnimationSetting.LedDirection;

            commands[36] = (byte)ScrollWheelMode;

            for (int i = 0; i < CurrentFwAnimationSetting.LedColors.Length; i++)
            {
                commands[12 + i * 3] = CurrentFwAnimationSetting.LedColors[i].R;
                commands[13 + i * 3] = CurrentFwAnimationSetting.LedColors[i].G;
                commands[14 + i * 3] = CurrentFwAnimationSetting.LedColors[i].B;
            }

            Array.Copy(ScrollWheelFunctions.Commands, 0, commands, 37, 8);

            return commands;
        }

        public void SetScrollwheelMode(ScrollwheelMode scrollWheelMode)
        {
            ScrollWheelMode = scrollWheelMode;
        }

        public void SetLedBrightness(int percentage)
        {
            _ledBrightnessPercentage = percentage;
        }

        public void SetLedOnOff(bool isLedOn)
        {
            LedOn = isLedOn;
        }
    }
}
