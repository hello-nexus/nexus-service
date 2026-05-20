using System;

namespace Qos.Service.Peripherals.Keeb.Hid.Layout
{
    public readonly struct KeebLayoutModel(Keyboard ledKey, MatrixPosition? fwMatrixPosition, LedPosition ledPosition, MatrixPosition? matrixPosition)
    {
        public readonly Keyboard LED_KEY { get; } = ledKey;
        public readonly MatrixPosition FwMatrixPosition { get; } = fwMatrixPosition ?? MatrixPosition.Empty;
        public readonly MatrixPosition MatrixPosition { get; } = matrixPosition ?? MatrixPosition.Empty;
        public readonly LedPosition LedPosition { get; } = ledPosition;
    }

    public readonly struct MatrixPosition(int x, int y) : IEquatable<MatrixPosition>
    {
        public readonly int X { get; } = x;
        public readonly int Y { get; } = y;
        public static readonly MatrixPosition Empty = new(-1, -1);

        public bool Equals(MatrixPosition other) => X.Equals(other.X) && Y.Equals(other.Y);
        public override bool Equals(object? obj) => obj is MatrixPosition mp && Equals(mp);
        public static bool operator ==(MatrixPosition left, MatrixPosition right) => left.Equals(right);
        public static bool operator !=(MatrixPosition left, MatrixPosition right) => !(left == right);
        public override int GetHashCode() => HashCode.Combine(X, Y);
    }

    public readonly struct LedPosition(int y, int x, int commandsIndex)
    {
        public readonly int X { get; } = x;
        public readonly int Y { get; } = y;
        public readonly int CommandsIndex { get; } = commandsIndex;
    }
}
