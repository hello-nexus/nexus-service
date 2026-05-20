using Qos.Service.Peripherals.Keeb.Hid.Enums;
using Qos.Service.Peripherals.Keeb.Hid.Layout;

namespace Qos.Service.Peripherals.Keeb.Hid.KeyAssignment
{
    public class KeyLayer(KeebLayout layout)
    {
        private readonly Key[][] _currentMapping = layout == KeebLayout.ISO
                ? KeyAssignmentDefaults.ISO_DEFAULT_LAYER1
                : KeyAssignmentDefaults.ANSI_DEFAULT_LAYER1;

        /// <summary>
        /// Do not change this map directly
        /// Please call the function to change it to store on fw
        /// </summary>
        public Key[][] CurrentMapping
        {
            get => (Key[][])(_currentMapping?.Clone()!);
        }

        private readonly KeebLayout _layout = layout;

        /// <summary>
        /// Get commands of default mapping
        /// </summary>
        /// <returns></returns>
        public byte[] GetDefaultKeyMapCommands(int layer)
        {
            byte[] allCommands = new byte[512]; //126 * 4
            Key[][] DefaultKeyAssignment = _layout == KeebLayout.ANSI
                                            ? (layer == 0 ? KeyAssignmentDefaults.ANSI_DEFAULT_LAYER1 : KeyAssignmentDefaults.ANSI_DEFAULT_LAYER_OTHERS)
                                            : (layer == 0 ? KeyAssignmentDefaults.ISO_DEFAULT_LAYER1 : KeyAssignmentDefaults.ISO_DEFAULT_LAYER_OTHERS);
            int[][] indexLayout = _layout == KeebLayout.ANSI ? KeyAssignmentDefaults.ANSI_COMMAND_INDEX : KeyAssignmentDefaults.ISO_COMMAND_INDEX;

            for (int i = 0; i < DefaultKeyAssignment.Length; i++)
            {
                for (int j = 0; j < DefaultKeyAssignment[i].Length; j++)
                {
                    int index = indexLayout[i][j] - 1;
                    // Cache Commands to avoid a defensive Clone() per index access.
                    byte[] cmds = DefaultKeyAssignment[i][j].Commands;
                    allCommands[index * 4] = cmds[0];
                    allCommands[index * 4 + 1] = cmds[1];
                    allCommands[index * 4 + 2] = cmds[2];
                    allCommands[index * 4 + 3] = cmds[3];
                }
            }

            // Return the working buffer directly — no second allocation needed.
            return allCommands;
        }

        /// <summary>
        /// Get commands of current keymapping
        /// 126 key * 4 bytes = 512 bytes
        /// </summary>
        /// <returns>byte[512]</returns>
        public byte[] GetCurrentKeyMapCommands()
        {
            byte[] allCommands = new byte[512]; //126 * 4
            int[][] indexLayout = _layout == KeebLayout.ANSI ? KeyAssignmentDefaults.ANSI_COMMAND_INDEX : KeyAssignmentDefaults.ISO_COMMAND_INDEX;

            // Use _currentMapping directly to avoid the defensive Clone() on each property access.
            for (int i = 0; i < _currentMapping.Length; i++)
            {
                for (int j = 0; j < _currentMapping[i].Length; j++)
                {
                    int index = indexLayout[i][j] - 1;
                    // Cache Commands to avoid a defensive Clone() per index access.
                    byte[] cmds = _currentMapping[i][j].Commands;
                    allCommands[index * 4] = cmds[0];
                    allCommands[index * 4 + 1] = cmds[1];
                    allCommands[index * 4 + 2] = cmds[2];
                    allCommands[index * 4 + 3] = cmds[3];
                }
            }

            // Return the working buffer directly — no second allocation needed.
            return allCommands;
        }

        public void GetCurrentLayoutFromFw(byte[] commands)
        {
            int[][] indexLayout = _layout == KeebLayout.ANSI ? KeyAssignmentDefaults.ANSI_COMMAND_INDEX : KeyAssignmentDefaults.ISO_COMMAND_INDEX;

            // Use _currentMapping directly to avoid the defensive Clone() on each property access.
            for (int i = 0; i < _currentMapping.Length; i++)
            {
                for (int j = 0; j < _currentMapping[i].Length; j++)
                {
                    int index = indexLayout[i][j] - 1;
                    byte[] keyCommands =
                    [
                        commands[index * 4],
                        commands[index * 4 + 1],
                        commands[index * 4 + 2],
                        commands[index * 4 + 3]
                    ];

                    _currentMapping[i][j] = new Key(keyCommands);
                }
            }
        }

        public void ChangeCurrentLayerKeyMap(int x, int y, Key thisKey)
        {
            _currentMapping[x][y] = thisKey;
        }
    }
}
