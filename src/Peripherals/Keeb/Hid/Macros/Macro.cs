using System.Collections.Generic;

namespace Qos.Service.Peripherals.Keeb.Hid.Macros
{
    public class Macro
    {
        private int _repeat;

        /// <summary>
        /// Macro Index
        /// </summary>
        public int Index { private set; get; }
        public int RepeatCount
        {
            private set
            {
                _repeat = value;
                byte repeat_h = (byte)(_repeat / 256);
                byte repeat_l = (byte)(_repeat % 256);

                if (_commands != null && _commands.Count > 1)
                {
                    _commands[0] = repeat_l;
                    _commands[1] = repeat_h;
                }
            }
            get => _repeat;
        }

        public List<KeyCode> Keys { private set; get; } = new List<KeyCode>();
        public List<byte> Commands { get => _commands != null ? new List<byte>(_commands) : null; }

        private readonly List<byte> _commands;
        private const byte FOUR_BYTES_DURATION_MAKE = 0x7F;
        private const byte FOUR_BYTES_DURATION_BREAK = 0xFF;
        private const byte NONE_BYTE = 0x00;

        public Macro(int num, int repeat, List<KeyCode> keys)
        {
            Index = num;
            RepeatCount = repeat;
            Keys = keys;
            _commands = new List<byte>();

            byte repeat_h = (byte)(repeat / 256);
            byte repeat_l = (byte)(repeat % 256);
            _commands.Add(repeat_l);
            _commands.Add(repeat_h);

            for (int i = 0; i < Keys.Count; i++)
            {
                _commands.AddRange(Keys[i].Commands);
            }
        }

        public Macro(int num, List<byte> commands)
        {
            Index = num;
            _commands = commands != null ? new List<byte>(commands) : null;
            RepeatCount = _commands[0] + _commands[1] * 256;

            for (int i = 2; i < _commands.Count; i += 2)
            {
                int commandCount = 2;
                if (_commands[i] == NONE_BYTE && _commands[i + 1] == NONE_BYTE)
                {
                    break;
                }
                else if (_commands[i] == FOUR_BYTES_DURATION_MAKE || _commands[i] == FOUR_BYTES_DURATION_BREAK)
                {
                    commandCount += 2;
                }
                KeyCode thisKey = new(_commands.GetRange(i, commandCount).ToArray());
                Keys.Add(thisKey);

                if (_commands[i] == FOUR_BYTES_DURATION_MAKE || _commands[i] == FOUR_BYTES_DURATION_BREAK)
                {
                    i += 2;
                }
            }
        }
    }
}
