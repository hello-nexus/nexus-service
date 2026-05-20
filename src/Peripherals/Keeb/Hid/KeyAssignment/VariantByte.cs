namespace Qos.Service.Peripherals.Keeb.Hid.KeyAssignment
{
    public readonly struct VariantByte(bool isVariant)
    {
        public readonly bool IsVariant { get; } = isVariant;
    }
}
