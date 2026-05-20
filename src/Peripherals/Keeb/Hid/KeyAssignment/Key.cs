using System.Collections.Generic;
using Qos.Service.Peripherals.Keeb.Hid.Enums;

namespace Qos.Service.Peripherals.Keeb.Hid.KeyAssignment;

/// <summary>
/// A single key assignment: the function name (e.g. <see cref="KeyFunction.Macro1"/>),
/// the assignment mode it falls under, and the 4 raw bytes the firmware writes
/// into its layer map for that key.
///
/// Two constructors:
/// 1. <c>Key(KeyFunction, byte[] inputs, int profileIndex)</c> — builds a key from
///    a function + optional parameter inputs (e.g. layer index for MOSwitch,
///    profile index for ProfileValue, mouse-step for MouseXPanLeft). Looks up the
///    base bytes from <see cref="KeyFunctionByteDictionary"/> and patches the
///    parameter slots described by <see cref="KeyFunctionModel.ParameterByteIndexs"/>.
/// 2. <c>Key(byte[] commands)</c> — reverse decode of 4 firmware bytes back into a
///    <see cref="KeyFunction"/>. Used when reading a layer back from the device.
/// </summary>
public class Key
{
    public string KeyName { get; private set; } = "";
    public KeyAssignmentMode Mode { get; private set; }

    public KeyFunction KeyFunction
    {
        get => _keyFunction;
        private set
        {
            _keyFunction = value;
            KeyName = value.ToString();
        }
    }

    public byte[]? Commands => (byte[]?)_commands?.Clone();

    private KeyFunction _keyFunction;
    private byte[]? _commands;

    public Key(KeyFunction keyFunction, byte[]? inputs, int profileIndex)
    {
        KeyFunction = keyFunction;
        CheckKeyFunctionMode(keyFunction);

        foreach (var functionByteDictionary in KeyFunctionByteDictionary.PROFILE_FUNCTION_BYTE[(KeebProfile)profileIndex].Keys)
        {
            if (functionByteDictionary.TryGetValue(KeyFunction, out var value))
            {
                _commands = (byte[]?)value?.Clone();
                break;
            }
        }

        var model = KeyFunctionLibrary.Library[Mode].Models[KeyFunction];
        if (model.ParameterByteIndexs != null && inputs != null && _commands != null)
        {
            for (int i = 0; i < model.ParameterByteIndexs.Length && i < inputs.Length; i++)
            {
                _commands[model.ParameterByteIndexs[i]] = inputs[i];
            }
        }
    }

    public Key(byte[] commands)
    {
        _commands = (byte[])commands.Clone();
        GetKeyCodeFromByte(_commands);
    }

    private void CheckKeyFunctionMode(KeyFunction keyFunction)
    {
        foreach (var category in KeyAssignmentFunctionList.KEYFUNCTION_CATEGORIES)
        {
            if (category.Value.Contains(keyFunction))
            {
                Mode = category.Key;
                break;
            }
        }
    }

    private void GetKeyCodeFromByte(byte[] commands)
    {
        bool keyChecked = false;

        foreach (var variantByteDictionary in KeyFunctionByteDictionary.ALL_PROFILE_FUNCTION_KEY_CHANGING_BYTE_INDEX)
        {
            if (keyChecked) break;
            VariantByte[] variant = variantByteDictionary.Value;
            foreach (var keyCodeSet in variantByteDictionary.Key)
            {
                var functionBytes = keyCodeSet.Value;
                bool isTheSame = true;
                for (int i = 0; i < variant.Length; i++)
                {
                    if (!variant[i].IsVariant)
                    {
                        if (functionBytes[i] != commands[i]) { isTheSame = false; break; }
                    }
                }
                if (isTheSame)
                {
                    KeyFunction = keyCodeSet.Key;
                    keyChecked = true;
                    break;
                }
            }
        }

        if (!keyChecked)
        {
            KeyFunction = KeyFunction.None;
            _commands = new byte[4] { 0, 0, 0, 0 };
        }

        CheckKeyFunctionMode(KeyFunction);
    }
}
