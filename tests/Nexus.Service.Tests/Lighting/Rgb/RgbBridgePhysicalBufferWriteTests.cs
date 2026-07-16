using Nexus.Service.Lighting.Engine;
using Nexus.Service.Lighting.Rgb;

namespace Nexus.Service.Tests.Lighting.Rgb;

/// <summary>
/// See <see cref="RgbBridge.IsBridgeFrame"/> for why OnFrame gates its
/// per-physical-buffer write on frame id, not just PhysicalIndex.
/// </summary>
public class RgbBridgePhysicalBufferWriteTests
{
    [Fact]
    public void Bridge_frame_is_eligible_to_write_its_own_physical_buffer()
    {
        var bridgeFrame = new DeviceFrame(index: 0, id: "openrgb-l-COM5", ledCount: 4, physicalIndex: 1);
        var bridgeFrameIds = new HashSet<string> { "openrgb-l-COM5" };

        Assert.True(RgbBridge.IsBridgeFrame(bridgeFrame, bridgeFrameIds));
    }

    [Fact]
    public void Contributor_frame_sharing_a_physical_index_is_not_eligible_to_write()
    {
        // As if device index 0 were excluded by first-party ownership, so
        // this is the only OpenRGB frame built (physical index 1), and a
        // contributor frame reuses engine ordinal 1 as its own
        // PhysicalIndex - the exact collision this check prevents.
        var bridgeFrame = new DeviceFrame(index: 0, id: "openrgb-l-COM5", ledCount: 4, physicalIndex: 1);
        var contributorFrame = new DeviceFrame(index: 1, id: "np50:SER1:port1:dev0", ledCount: 6);
        Assert.Equal(bridgeFrame.PhysicalIndex, contributorFrame.PhysicalIndex);

        var bridgeFrameIds = new HashSet<string> { "openrgb-l-COM5" };

        Assert.False(RgbBridge.IsBridgeFrame(contributorFrame, bridgeFrameIds));
    }

    [Fact]
    public void Split_motherboard_zone_frame_is_eligible_to_write()
    {
        var zoneFrame = new DeviceFrame(index: 0, id: "openrgb-s-MB01-0", ledCount: 4, physicalIndex: 0);
        var bridgeFrameIds = new HashSet<string> { "openrgb-s-MB01-0", "openrgb-s-MB01-1" };

        Assert.True(RgbBridge.IsBridgeFrame(zoneFrame, bridgeFrameIds));
    }

    [Fact]
    public void Colliding_contributor_frame_bytes_do_not_land_in_the_bridge_devices_buffer()
    {
        // Simulates the OnFrame write gate: a physical buffer keyed by
        // PhysicalIndex, one bridge frame and one colliding contributor
        // frame both mapped to it. Only the bridge frame's write should be
        // allowed through the gate before touching the buffer.
        var buffer = new RgbColor[4];
        var untouched = (RgbColor[])buffer.Clone();

        var bridgeFrame = new DeviceFrame(index: 0, id: "openrgb-l-COM5", ledCount: 4, physicalIndex: 1);
        var contributorFrame = new DeviceFrame(index: 1, id: "keeb:SER1:underglow", ledCount: 4, physicalIndex: 1);
        var bridgeFrameIds = new HashSet<string> { "openrgb-l-COM5" };

        void WriteIfEligible(DeviceFrame dev, RgbColor color)
        {
            if (!RgbBridge.IsBridgeFrame(dev, bridgeFrameIds))
            {
                return;
            }
            for (var i = 0; i < buffer.Length; i++)
            {
                buffer[i] = color;
            }
        }

        WriteIfEligible(contributorFrame, new RgbColor(255, 0, 0));
        Assert.Equal(untouched, buffer);

        WriteIfEligible(bridgeFrame, new RgbColor(0, 255, 0));
        Assert.All(buffer, c => Assert.Equal(new RgbColor(0, 255, 0), c));
    }
}
