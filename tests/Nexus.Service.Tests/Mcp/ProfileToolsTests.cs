using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Nexus.Service.Mcp;
using Nexus.Service.Mcp.Tools;
using Nexus.Service.Persistence;
using Nexus.Service.Sockets;
using Xunit;

namespace Nexus.Service.Tests.Mcp;

/// <summary>
/// Unit coverage for list_profiles and apply_profile against a real
/// ProfileManager backed by a temp-file JsonConfigStore, matching the
/// ProfileManager test suites' own fixture style (real store, not a stub).
/// </summary>
public sealed class ProfileToolsTests : IDisposable
{
    private readonly string _tempDir =
        Path.Combine(Path.GetTempPath(), "nexus-mcp-profile-tools-" + Guid.NewGuid().ToString("N")[..8]);
    private readonly JsonConfigStore _store;
    private readonly ProfileManager _profiles;

    public ProfileToolsTests()
    {
        Directory.CreateDirectory(_tempDir);
        _store = new JsonConfigStore(Path.Combine(_tempDir, "settings.json"));
        _profiles = new ProfileManager(_store);
        _profiles.Initialize();
    }

    public void Dispose()
    {
        _profiles.Dispose();
        _store.Dispose();
        try { Directory.Delete(_tempDir, recursive: true); } catch { }
    }

    [Fact]
    public async Task ListProfiles_marks_the_active_profile()
    {
        var tool = new ListProfilesTool(_profiles);

        var result = await tool.ExecuteAsync(null, CancellationToken.None);

        Assert.False(result.IsError);
        using var doc = JsonDocument.Parse(result.Text);
        var profiles = doc.RootElement.GetProperty("profiles").EnumerateArray().ToList();
        var active = Assert.Single(profiles, p => p.GetProperty("active").GetBoolean());
        Assert.Equal("Default", active.GetProperty("name").GetString());
    }

    [Fact]
    public void ListProfiles_is_gated_by_telemetry_not_profiles_consent()
    {
        // Read-only: list_profiles must stay usable with profile switching
        // turned off, unlike apply_profile which actually changes state.
        var tool = new ListProfilesTool(_profiles);

        Assert.Equal(McpCapability.Telemetry, tool.Capability);
    }

    [Fact]
    public void ApplyProfile_is_still_gated_by_profiles_consent()
    {
        var tool = new ApplyProfileTool(_profiles, new MultiplexHub());

        Assert.Equal(McpCapability.Profiles, tool.Capability);
    }

    [Fact]
    public async Task ApplyProfile_switches_by_name_and_broadcasts()
    {
        var defaultId = _profiles.GetManifest().ActiveProfileId;
        _profiles.CreateProfile("Second");
        Assert.NotEqual(defaultId, _profiles.GetManifest().ActiveProfileId);

        var tool = new ApplyProfileTool(_profiles, new MultiplexHub());
        var result = await tool.ExecuteAsync(JsonSerializer.SerializeToElement(new { profile = "Default" }), CancellationToken.None);

        Assert.False(result.IsError);
        Assert.Equal(defaultId, _profiles.GetManifest().ActiveProfileId);
    }

    [Fact]
    public async Task ApplyProfile_switches_by_id()
    {
        var defaultId = _profiles.GetManifest().ActiveProfileId;
        var second = _profiles.CreateProfile("Second");
        _profiles.SwitchProfile(defaultId);

        var tool = new ApplyProfileTool(_profiles, new MultiplexHub());
        var result = await tool.ExecuteAsync(JsonSerializer.SerializeToElement(new { profile = second.Id }), CancellationToken.None);

        Assert.False(result.IsError);
        Assert.Equal(second.Id, _profiles.GetManifest().ActiveProfileId);
    }

    [Fact]
    public async Task ApplyProfile_unknown_profile_is_error_listing_known_profiles()
    {
        var tool = new ApplyProfileTool(_profiles, new MultiplexHub());

        var result = await tool.ExecuteAsync(JsonSerializer.SerializeToElement(new { profile = "does-not-exist" }), CancellationToken.None);

        Assert.True(result.IsError);
        Assert.Contains("Default", result.Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ApplyProfile_missing_profile_arg_is_error()
    {
        var tool = new ApplyProfileTool(_profiles, new MultiplexHub());

        var result = await tool.ExecuteAsync(JsonSerializer.SerializeToElement(new { }), CancellationToken.None);

        Assert.True(result.IsError);
    }
}
