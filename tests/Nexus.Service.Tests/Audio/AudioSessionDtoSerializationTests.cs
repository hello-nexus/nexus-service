using System.Text.Json;
using Nexus.Service.Models.Activity;
using Nexus.Service.Serialization;
using Xunit;

namespace Nexus.Service.Tests.Audio;

/// <summary>
/// The audioMixer.snapshot helper can outlive a service update: a payload
/// from an older helper build predates DeviceIds/OnDefault. Deserializing it
/// must still surface the strip (OnDefault true), not silently hide it from
/// the default-output-filtered mixer.
/// </summary>
public class AudioSessionDtoSerializationTests
{
    private const string OldHelperJson = """
        [
            { "id": "spotify", "name": "Spotify", "volume": 0.8, "muted": false, "peak": 0, "active": true }
        ]
        """;

    [Fact]
    public void MissingOnDefaultAndDeviceIdsFallBackToShowingTheStrip()
    {
        var sessions = JsonSerializer.Deserialize(OldHelperJson, AppJsonContext.Default.ListAudioSessionDto);

        var strip = Assert.Single(sessions!);
        Assert.Equal("spotify", strip.Id);
        Assert.True(strip.OnDefault);
        Assert.Empty(strip.DeviceIds);
    }

    [Fact]
    public void ExplicitFieldsRoundTrip()
    {
        var strip = new AudioSessionDto
        {
            Id = "spotify",
            Name = "Spotify",
            Volume = 0.8,
            OnDefault = false,
            DeviceIds = { "endpoint-a", "endpoint-b" },
        };

        var json = JsonSerializer.Serialize(strip, AppJsonContext.Default.AudioSessionDto);
        var roundTripped = JsonSerializer.Deserialize(json, AppJsonContext.Default.AudioSessionDto);

        Assert.False(roundTripped!.OnDefault);
        Assert.Equal(new[] { "endpoint-a", "endpoint-b" }, roundTripped.DeviceIds);
    }
}
