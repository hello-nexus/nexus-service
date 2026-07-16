using Nexus.Service.Lighting;

namespace Nexus.Service.Tests;

public class LightingDeviceControlledTests : IDisposable
{
    private readonly string _tempDir;
    private readonly string _settingsPath;
    private readonly TestableConfigStore _store;

    public LightingDeviceControlledTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "nexus-test-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_tempDir);
        _settingsPath = Path.Combine(_tempDir, "settings.json");
        _store = new TestableConfigStore(_settingsPath);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); } catch { }
    }

    [Fact]
    public void SetControlled_False_AddsToUncontrolledList()
    {
        LightingControlledState.SetControlled("openrgb-3", false, _store);

        Assert.Contains("openrgb-3", _store.Load().Devices.UncontrolledLightingDevices);
    }

    [Fact]
    public void SetControlled_True_RemovesFromUncontrolledList()
    {
        LightingControlledState.SetControlled("openrgb-3", false, _store);
        LightingControlledState.SetControlled("openrgb-3", true, _store);

        Assert.DoesNotContain("openrgb-3", _store.Load().Devices.UncontrolledLightingDevices);
    }

    [Fact]
    public void SetControlled_False_IsIdempotent()
    {
        LightingControlledState.SetControlled("openrgb-3", false, _store);
        LightingControlledState.SetControlled("openrgb-3", false, _store);

        var list = _store.Load().Devices.UncontrolledLightingDevices;
        Assert.Single(list, id => id == "openrgb-3");
    }

    [Fact]
    public void SetControlled_True_IsIdempotent()
    {
        LightingControlledState.SetControlled("openrgb-3", true, _store);
        LightingControlledState.SetControlled("openrgb-3", true, _store);

        Assert.Empty(_store.Load().Devices.UncontrolledLightingDevices);
    }

    [Fact]
    public void SetControlled_OnlyTouchesGivenId()
    {
        LightingControlledState.SetControlled("openrgb-0", false, _store);
        LightingControlledState.SetControlled("openrgb-1", false, _store);
        LightingControlledState.SetControlled("openrgb-0", true, _store);

        var list = _store.Load().Devices.UncontrolledLightingDevices;
        Assert.DoesNotContain("openrgb-0", list);
        Assert.Contains("openrgb-1", list);
    }

    [Fact]
    public void SetControlled_ReplacesListReference_NotMutatesInPlace()
    {
        LightingControlledState.SetControlled("openrgb-0", false, _store);
        var before = _store.Load().Devices.UncontrolledLightingDevices;

        LightingControlledState.SetControlled("openrgb-1", false, _store);
        var after = _store.Load().Devices.UncontrolledLightingDevices;

        Assert.NotSame(before, after);
        Assert.DoesNotContain("openrgb-1", before);
        Assert.Contains("openrgb-1", after);
    }

    [Fact]
    public void SetControlled_UndisturbedDisabledList()
    {
        _store.Update(s => s.Devices.DisabledLightingDevices = new List<string> { "openrgb-5" });

        LightingControlledState.SetControlled("openrgb-5", false, _store);

        var settings = _store.Load();
        Assert.Contains("openrgb-5", settings.Devices.DisabledLightingDevices);
        Assert.Contains("openrgb-5", settings.Devices.UncontrolledLightingDevices);
    }
}
