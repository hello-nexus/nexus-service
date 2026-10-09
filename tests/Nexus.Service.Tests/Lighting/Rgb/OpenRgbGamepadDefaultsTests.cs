using System;
using System.Collections.Generic;
using Nexus.Service.Lighting.Rgb;
using Nexus.Service.Persistence;

namespace Nexus.Service.Tests.Lighting.Rgb;

public class OpenRgbGamepadDefaultsTests
{
    private static readonly string DualSenseKey = OpenRgbGamepadDefaults.KeyOf("Sony DualSense");

    private static HashSet<string> Present(params string[] detectors) => new(detectors, StringComparer.Ordinal);

    [Fact]
    public void Every_controller_detector_is_held_off_on_a_fresh_install()
    {
        var held = OpenRgbGamepadDefaults.HeldOffDetectors(new NexusSettings());

        Assert.Equal(new[] { "GameSir Nova 2 Lite", "Sony DualSense", "Sony DualSense Edge", "Sony DualShock 4" }, held);
    }

    [Fact]
    public void An_allowed_detector_is_no_longer_held_off()
    {
        var settings = new NexusSettings();
        settings.Devices.OpenRgbGamepadDetectorsAllowed.Add("Sony DualSense");

        Assert.DoesNotContain("Sony DualSense", OpenRgbGamepadDefaults.HeldOffDetectors(settings));
        Assert.False(OpenRgbGamepadDefaults.IsHeldOff(settings, 0x054C, 0x0CE6));
        Assert.True(OpenRgbGamepadDefaults.IsHeldOff(settings, 0x054C, 0x0DF2));
    }

    [Fact]
    public void Present_controller_gets_an_uncontrolled_excluded_card()
    {
        var settings = new NexusSettings();

        Assert.True(OpenRgbGamepadDefaults.PresenceChanges(settings, Present("Sony DualSense")));
        Assert.True(OpenRgbGamepadDefaults.ApplyPresence(settings, Present("Sony DualSense")));

        var snap = settings.Devices.OpenRgbDetectorExclusions[DualSenseKey];
        Assert.Equal("Sony DualSense", snap.DetectorName);
        Assert.Equal(10u, snap.Type);
        Assert.Contains(DualSenseKey, settings.Devices.UncontrolledLightingDevices);
        Assert.False(OpenRgbGamepadDefaults.PresenceChanges(settings, Present("Sony DualSense")));
    }

    [Fact]
    public void Unplugged_controller_card_is_dropped()
    {
        var settings = new NexusSettings();
        OpenRgbGamepadDefaults.ApplyPresence(settings, Present("Sony DualSense"));

        Assert.True(OpenRgbGamepadDefaults.ApplyPresence(settings, Present()));

        Assert.Empty(settings.Devices.OpenRgbDetectorExclusions);
        Assert.Empty(settings.Devices.UncontrolledLightingDevices);
    }

    [Fact]
    public void Allowed_controller_gets_no_card()
    {
        var settings = new NexusSettings();
        settings.Devices.OpenRgbGamepadDetectorsAllowed.Add("Sony DualSense");

        Assert.False(OpenRgbGamepadDefaults.ApplyPresence(settings, Present("Sony DualSense")));
        Assert.Empty(settings.Devices.OpenRgbDetectorExclusions);
    }

    [Fact]
    public void Turning_the_card_on_allows_the_detector_and_lifts_the_exclusion()
    {
        var settings = new NexusSettings();
        OpenRgbGamepadDefaults.ApplyPresence(settings, Present("Sony DualSense"));
        // What the /controlled route does on a click.
        OpenRgbGamepadDefaults.AllowFor(settings, DualSenseKey);
        settings.Devices.UncontrolledLightingDevices.Remove(DualSenseKey);

        OpenRgbDetectorExclusions.Apply(settings, OpenRgbDetectorExclusions.Compute(Array.Empty<RgbDevice>(), settings));

        Assert.Empty(settings.Devices.OpenRgbDetectorExclusions);
        Assert.Equal(new[] { "Sony DualSense" }, settings.Devices.OpenRgbGamepadDetectorsAllowed);
        Assert.False(OpenRgbGamepadDefaults.ApplyPresence(settings, Present("Sony DualSense")));
    }

    [Fact]
    public void Preset_switch_dropping_the_key_never_allows_the_detector()
    {
        var settings = new NexusSettings();
        OpenRgbGamepadDefaults.ApplyPresence(settings, Present("Sony DualSense"));
        // A layout preset replaces the uncontrolled list wholesale.
        settings.Devices.UncontrolledLightingDevices = new List<string>();

        var delta = OpenRgbDetectorExclusions.Compute(Array.Empty<RgbDevice>(), settings);

        Assert.True(delta.IsEmpty);
        Assert.Empty(settings.Devices.OpenRgbGamepadDetectorsAllowed);
        Assert.True(OpenRgbGamepadDefaults.PresenceChanges(settings, Present("Sony DualSense")));
        Assert.True(OpenRgbGamepadDefaults.ApplyPresence(settings, Present("Sony DualSense")));
        Assert.Contains(DualSenseKey, settings.Devices.UncontrolledLightingDevices);
    }

    [Fact]
    public void Earlier_user_exclusion_keeps_its_own_card_and_allows_on_turn_on()
    {
        var settings = new NexusSettings();
        settings.Devices.OpenRgbDetectorExclusions["openrgb-s-DS5"] = new OpenRgbDetectorExclusion { DetectorName = "Sony DualSense", Type = 10, LedCount = 1 };
        settings.Devices.UncontrolledLightingDevices.Add("openrgb-s-DS5");

        Assert.False(OpenRgbGamepadDefaults.ApplyPresence(settings, Present("Sony DualSense")));
        OpenRgbGamepadDefaults.AllowFor(settings, "openrgb-s-DS5");

        Assert.Equal(new[] { "Sony DualSense" }, settings.Devices.OpenRgbGamepadDetectorsAllowed);
    }

    [Fact]
    public void Turning_on_another_device_allows_no_controller()
    {
        var settings = new NexusSettings();
        settings.Devices.OpenRgbDetectorExclusions["openrgb-s-K70A"] = new OpenRgbDetectorExclusion { DetectorName = "Corsair K70 RGB" };

        OpenRgbGamepadDefaults.AllowFor(settings, "openrgb-s-K70A");

        Assert.Empty(settings.Devices.OpenRgbGamepadDetectorsAllowed);
    }

    [Fact]
    public void Card_renders_as_an_off_gamepad()
    {
        var settings = new NexusSettings();
        OpenRgbGamepadDefaults.ApplyPresence(settings, Present("Sony DualSense"));

        var card = Assert.Single(OpenRgbZoneSupport.BuildCards(Array.Empty<RgbDevice>(), settings, isInit: false).Devices);

        Assert.Equal(DualSenseKey, card.Id);
        Assert.Equal("Sony DualSense", card.Name);
        Assert.Equal("gamepad", card.Type);
    }

    [Fact]
    public void Held_off_arrival_parses_from_a_usb_key()
    {
        Assert.True(UsbTopologyFilter.TryParseVidPid("054C:0CE6:serial", out var vid, out var pid));
        Assert.True(OpenRgbGamepadDefaults.IsHeldOff(new NexusSettings(), vid, pid));
    }
}
