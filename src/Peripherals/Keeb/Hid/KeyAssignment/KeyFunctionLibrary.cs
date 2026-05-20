using System;
using System.Collections.Generic;

namespace Qos.Service.Peripherals.Keeb.Hid.KeyAssignment
{
    /// <summary>
    /// Static class holding the shared key function lookup table.
    /// Initialized once at class-load time; eliminates the per-Key instance
    /// that previously caused ~21 MB of duplicate KeyFunctionList / KeyFunctionModel objects.
    /// </summary>
    public static class KeyFunctionLibrary
    {
        /// <summary>Maps each <see cref="KeyAssignmentMode"/> to its list of supported key function models.</summary>
        public static readonly Dictionary<KeyAssignmentMode, KeyFunctionList> Library;

        static KeyFunctionLibrary()
        {
            Library = [];
            foreach (KeyAssignmentMode mode in Enum.GetValues<KeyAssignmentMode>())
            {
                Library.Add(mode, new KeyFunctionList(mode));
            }
        }
    }

    public class KeyFunctionList
    {
        public KeyAssignmentMode Mode { private set; get; }
        public Dictionary<KeyFunction, KeyFunctionModel> Models { private set; get; } = [];

        public KeyFunctionList(KeyAssignmentMode mode)
        {
            Mode = mode;
            List<KeyFunction> currentFunctionList = KeyAssignmentFunctionList.KEYFUNCTION_CATEGORIES.GetValueOrDefault(mode);

            foreach (KeyFunction thisFunction in currentFunctionList)
            {
                if (KeyFunctionByteDictionary.KEY_FUNCTION_INPUT_TYPE.TryGetValue(thisFunction, out KeyFunctionModel value))
                {
                    Models.Add(thisFunction, value);
                }
                else
                {
                    Models.Add(thisFunction, new KeyFunctionModel(thisFunction));
                }
            }
        }
    }
}
