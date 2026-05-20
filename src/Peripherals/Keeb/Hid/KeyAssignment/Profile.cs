using Qos.Service.Peripherals.Keeb.Hid.Enums;
using Qos.Service.Peripherals.Keeb.Hid.Macros;

namespace Qos.Service.Peripherals.Keeb.Hid.KeyAssignment
{
    public class Profile
    {
        public KeyLayer[] KeyLayers { get => (KeyLayer[])(_keylayers?.Clone()!); }
        public Macro[] Macros { get => (Macro[])(_macros?.Clone()!); }
        public int ProfileNum { get; private set; }

        private readonly Macro[] _macros = new Macro[16];
        private readonly KeyLayer[] _keylayers;

        public Profile(int profileNum, KeebLayout layout)
        {
            ProfileNum = profileNum;
            _keylayers = new KeyLayer[4] { new(layout), new(layout), new(layout), new(layout) };
        }

        public void WriteMacroInProfile(int macroIndex, Macro macro)
        {
            _macros[macroIndex] = macro;
        }

        public void GetLayerFromFw(int layer, byte[] commands)
        {
            _keylayers[layer].GetCurrentLayoutFromFw(commands);
        }

        public void ChangeLayerKeyMap(int layer, int x, int y, Key key)
        {
            _keylayers[layer].ChangeCurrentLayerKeyMap(x, y, key);
        }
    }
}
