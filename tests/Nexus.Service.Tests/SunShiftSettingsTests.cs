using Nexus.Service.Persistence;

namespace Nexus.Service.Tests;

/// <summary>
/// An unchosen sun shift resolves from the saved points wherever a document
/// is read: settings.json, a profile file and an imported profile. Profile
/// files skip the settings migrations, so the default is resolved at read time.
/// </summary>
public sealed class SunShiftSettingsTests : IDisposable
{
    private const string DefaultPoints = """[ {"hour":5,"brightness":10}, {"hour":7,"brightness":30}, {"hour":10,"brightness":100}, {"hour":17,"brightness":100}, {"hour":20,"brightness":30}, {"hour":22,"brightness":10} ]""";
    private const string CustomPoints = """[ {"hour":0,"brightness":10}, {"hour":12,"brightness":100} ]""";

    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), "nexus-sun-settings-" + Guid.NewGuid().ToString("N")[..8]);

    public SunShiftSettingsTests() => Directory.CreateDirectory(_tempDir);

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); } catch { }
    }

    private static string Doc(string? points) => points is null
        ? "{ }"
        : $$"""{ "lighting": { "brightnessSchedule": { "enabled": true, "points": {{points}} } } }""";

    [Theory]
    [InlineData(null, true)]
    [InlineData(DefaultPoints, true)]
    [InlineData(CustomPoints, false)]
    public void Settings_file_without_the_key_resolves_from_the_points(string? points, bool expected)
    {
        File.WriteAllText(Path.Combine(_tempDir, "settings.json"), Doc(points));
        using var store = new JsonConfigStore(Path.Combine(_tempDir, "settings.json"));

        Assert.Equal(expected, store.Load().Lighting.BrightnessSchedule.SunShiftOn);
    }

    [Theory]
    [InlineData(null, true)]
    [InlineData(DefaultPoints, true)]
    [InlineData(CustomPoints, false)]
    public void Profile_file_without_the_key_resolves_from_the_points_after_a_switch(string? points, bool expected)
    {
        using var store = new JsonConfigStore(Path.Combine(_tempDir, "settings.json"));
        using var profiles = new ProfileManager(store);
        profiles.Initialize();
        var target = profiles.CreateProfile("Target");
        profiles.CreateProfile("Other");
        File.WriteAllText(Path.Combine(_tempDir, $"profile-{target.Id}.json"), Doc(points));

        profiles.SwitchProfile(target.Id);

        Assert.Equal(expected, store.Load().Lighting.BrightnessSchedule.SunShiftOn);
    }

    [Theory]
    [InlineData(DefaultPoints, true)]
    [InlineData(CustomPoints, false)]
    public void Imported_profile_without_the_key_resolves_from_the_points(string points, bool expected)
    {
        using var store = new JsonConfigStore(Path.Combine(_tempDir, "settings.json"));
        using var profiles = new ProfileManager(store);
        profiles.Initialize();

        var imported = profiles.ImportProfileJson(Doc(points));
        profiles.SwitchProfile(imported.Id);

        Assert.Equal(expected, store.Load().Lighting.BrightnessSchedule.SunShiftOn);
    }

    [Fact]
    public void An_explicit_choice_beats_the_points()
    {
        File.WriteAllText(Path.Combine(_tempDir, "settings.json"),
            $$"""{ "lighting": { "brightnessSchedule": { "sunShift": true, "points": {{CustomPoints}} } } }""");
        using var store = new JsonConfigStore(Path.Combine(_tempDir, "settings.json"));

        Assert.True(store.Load().Lighting.BrightnessSchedule.SunShiftOn);
    }
}
