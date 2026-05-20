using System;

namespace Qos.Service.Peripherals.Keeb.Hid.KeyAssignment
{
    public class KeyFunctionModel
    {
        public KeyFunction KeyFunction { get; private set; }

        public Type[] DataTypes { get; private set; }

        public int[] ParameterByteIndexs { get; private set; }

        public string[] ParameterInputTypes { get; private set; }

        public KeyFunctionModel(KeyFunction keyFunction, Type[] dataTypes, int[] parameterByteIndexs, string[] parameterInputTypes)
        {
            KeyFunction = keyFunction;
            DataTypes = (Type[])(dataTypes?.Clone());
            ParameterByteIndexs = (int[])(parameterByteIndexs?.Clone());
            ParameterInputTypes = (string[])(parameterInputTypes?.Clone());
        }

        public KeyFunctionModel(KeyFunction keyFunction)
        {
            KeyFunction = keyFunction;
        }
    }
}
