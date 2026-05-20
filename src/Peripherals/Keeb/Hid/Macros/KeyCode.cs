using System;
using System.Collections;

namespace Qos.Service.Peripherals.Keeb.Hid.Macros
{
    public class KeyCode
    {
        public MacroKeyCategory Category { private set; get; }
        public KeyStatus ThisKeyStatus { private set; get; }
        public int Duration { private set; get; }
        public MacroKeyCode ThisKey { private set; get; }
        public byte[] Commands
        {
            get => _commands != null ? (byte[])_commands.Clone() : null;
        }

        private const byte FOUR_BYTES_DURATION_MAKE = 0x7F;
        private const byte FOUR_BYTES_DURATION_BREAK = 0xFF;
        private byte[] _commands;

        public KeyCode(KeyStatus keyStatus, int duration, MacroKeyCode thisKey)
        {
            ThisKeyStatus = keyStatus;
            Duration = duration;
            ThisKey = thisKey;

            GenerateCommands();
        }

        private void GenerateCommands()
        {
            if (Duration >= FOUR_BYTES_DURATION_MAKE)
            {
                _commands = new byte[4];
                _commands[0] = ThisKeyStatus == KeyStatus.Make ? FOUR_BYTES_DURATION_MAKE : FOUR_BYTES_DURATION_BREAK;
                _commands[2] = (byte)(Duration % 256); //Duration_low
                _commands[3] = (byte)(Duration / 256); //Duration_high
            }
            else
            {
                _commands = new byte[2];
                BitArray bits = new(8);
                bits[7] = ThisKeyStatus != KeyStatus.Make;
                string time = Convert.ToString(Duration, 2);

                for (int i = 0; i < time.Length; i++)
                {
                    bits[i] = time[time.Length - 1 - i] == '1';
                }

                bits.CopyTo(_commands, 0);
            }

            _commands[1] = (byte)ThisKey;
            GetKeyCategory();
        }

        private void GetKeyCategory()
        {
            foreach (var category in MacroFunctionList.MACROKEY_CATEGORIES)
            {
                if (category.Value.Contains(ThisKey))
                {
                    Category = category.Key;
                    break;
                }
            }
        }

        /// <summary>
        /// For controller to call to get KeyCode from FW bytes
        /// </summary>
        /// <param name="commands"></param>
        public KeyCode(byte[] commands)
        {
            _commands = commands != null ? (byte[])commands.Clone() : null;

            BitArray bits = new(new byte[] { commands[0] });
            ThisKeyStatus = bits[7] ? KeyStatus.Break : KeyStatus.Make;
            bits[7] = false;
            byte[] time = new byte[1];
            bits.CopyTo(time, 0);
            Duration = time[0];

            if (Duration == FOUR_BYTES_DURATION_MAKE)
            {
                Duration = commands[2] + commands[3] * 256;
            }

            ThisKey = (MacroKeyCode)commands[1];
            GetKeyCategory();
        }
    }
}
