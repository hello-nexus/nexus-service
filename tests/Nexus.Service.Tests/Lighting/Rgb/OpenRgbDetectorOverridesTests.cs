using System.Text.Json.Nodes;
using Nexus.Service.Lighting.Rgb;

namespace Nexus.Service.Tests.Lighting.Rgb;

/// <summary>
/// OpenRGB.json detector-override writing: the hardcoded first-party denylist
/// always lands as detectors:false; user exclusions land as detectors:false
/// plus membership in the service-owned placeholder_only array; names dropped
/// from that array get their detector re-enabled on the next write.
/// </summary>
public class OpenRgbDetectorOverridesTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("openrgb-cfg-").FullName;

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    private JsonObject ReadConfig()
    {
        var text = File.ReadAllText(Path.Combine(_dir, "OpenRGB.json"));
        return (JsonObject)JsonNode.Parse(text)!;
    }

    private static bool DetectorEnabled(JsonObject root, string name)
    {
        var map = (JsonObject)root["Detectors"]!["detectors"]!;
        return map[name] is JsonValue v && v.TryGetValue<bool>(out var b) && b;
    }

    private static List<string> PlaceholderOnly(JsonObject root)
    {
        var result = new List<string>();
        if (root["Detectors"]!["placeholder_only"] is JsonArray arr)
        {
            foreach (var node in arr)
            {
                result.Add(node!.GetValue<string>());
            }
        }
        return result;
    }

    [Fact]
    public void First_party_denylist_written_without_placeholders()
    {
        OpenRgbProcessManager.EnsureDetectorOverrides(_dir, Array.Empty<string>());

        var root = ReadConfig();
        Assert.False(DetectorEnabled(root, "HYTE Keeb TKL"));
        Assert.False(DetectorEnabled(root, "Corsair iCUE Link System Hub"));
        Assert.Empty(PlaceholderOnly(root));
    }

    [Fact]
    public void Hyte_nexus_detector_disabled_only_where_the_devices_are_driven_natively()
    {
        Assert.Contains("HYTE Nexus", OpenRgbProcessManager.BuildDisabledDetectors(macOS: false));
        Assert.DoesNotContain("HYTE Nexus", OpenRgbProcessManager.BuildDisabledDetectors(macOS: true));
        Assert.Contains("HYTE Keeb TKL", OpenRgbProcessManager.BuildDisabledDetectors(macOS: true));
    }

    [Fact]
    public void User_exclusion_lands_as_disabled_plus_placeholder_only()
    {
        OpenRgbProcessManager.EnsureDetectorOverrides(_dir, new[] { "Corsair K70 RGB" });

        var root = ReadConfig();
        Assert.False(DetectorEnabled(root, "Corsair K70 RGB"));
        Assert.Equal(new[] { "Corsair K70 RGB" }, PlaceholderOnly(root));
    }

    [Fact]
    public void Dropped_exclusion_reenables_the_detector()
    {
        OpenRgbProcessManager.EnsureDetectorOverrides(_dir, new[] { "Corsair K70 RGB" });
        OpenRgbProcessManager.EnsureDetectorOverrides(_dir, Array.Empty<string>());

        var root = ReadConfig();
        Assert.True(DetectorEnabled(root, "Corsair K70 RGB"));
        Assert.Empty(PlaceholderOnly(root));
    }

    [Fact]
    public void Dropped_exclusion_never_reenables_a_first_party_detector()
    {
        OpenRgbProcessManager.EnsureDetectorOverrides(_dir, new[] { "Corsair iCUE Link System Hub" });
        OpenRgbProcessManager.EnsureDetectorOverrides(_dir, Array.Empty<string>());

        var root = ReadConfig();
        Assert.False(DetectorEnabled(root, "Corsair iCUE Link System Hub"));
    }

    [Fact]
    public void Null_placeholder_list_leaves_placeholder_state_untouched()
    {
        // Null = settings unreadable at launch; re-enabling detectors whose
        // exclusions still exist would hand the hardware back to OpenRGB while
        // the UI keeps showing it ignored.
        OpenRgbProcessManager.EnsureDetectorOverrides(_dir, new[] { "Corsair K70 RGB" });
        OpenRgbProcessManager.EnsureDetectorOverrides(_dir, null);

        var root = ReadConfig();
        Assert.False(DetectorEnabled(root, "Corsair K70 RGB"));
        Assert.Equal(new[] { "Corsair K70 RGB" }, PlaceholderOnly(root));
    }

    [Fact]
    public void Foreign_config_content_is_preserved()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(Path.Combine(_dir, "OpenRGB.json"),
            /*lang=json*/ """{"Detectors":{"detectors":{"Razer Huntsman":true},"hid_safe_mode":true},"Client":{"port":123}}""");

        OpenRgbProcessManager.EnsureDetectorOverrides(_dir, new[] { "Corsair K70 RGB" });

        var root = ReadConfig();
        Assert.True(DetectorEnabled(root, "Razer Huntsman"));
        Assert.True(root["Detectors"]!["hid_safe_mode"]!.GetValue<bool>());
        Assert.Equal(123, root["Client"]!["port"]!.GetValue<int>());
    }

    [Fact]
    public void Unchanged_state_does_not_rewrite_the_file()
    {
        OpenRgbProcessManager.EnsureDetectorOverrides(_dir, new[] { "Corsair K70 RGB" });
        var path = Path.Combine(_dir, "OpenRGB.json");
        var before = File.GetLastWriteTimeUtc(path);
        File.SetLastWriteTimeUtc(path, before.AddHours(-1));

        OpenRgbProcessManager.EnsureDetectorOverrides(_dir, new[] { "Corsair K70 RGB" });
        Assert.Equal(before.AddHours(-1), File.GetLastWriteTimeUtc(path));
    }
}
