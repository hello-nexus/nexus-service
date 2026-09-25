namespace Nexus.Service.Peripherals.Galahad2;

/// <summary>
/// The pump-RGB command (0x83, ring/mode/brightness/speed/colors/direction) is identical
/// across the wired Trinity and the LCD variant - only which object holds the HID handle
/// differs. Galahad2Hub and the LCD's JpegPanelHub each implement this so the lighting
/// provider/writer can talk to whichever one is actually connected.
/// </summary>
public interface IGalahad2PumpTransport
{
    bool IsConnected { get; }
    bool SendLighting(byte ring, byte mode, byte brightness, byte speed, byte direction, System.ReadOnlySpan<byte> colors);
}
