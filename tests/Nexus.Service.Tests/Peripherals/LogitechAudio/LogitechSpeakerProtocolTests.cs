using System;
using System.Buffers.Binary;
using Nexus.Service.Peripherals.LogitechAudio;
using Xunit;

namespace Nexus.Service.Tests.Peripherals.LogitechAudio;

public class LogitechSpeakerProtocolTests
{
    [Fact]
    public void Bands_RoundTripAsLittleEndianFloats_AndClampToGHubRange()
    {
        var data = LogitechSpeakerProtocol.EncodeBands(new[] { 4f, 3f, 2f, 0f, -4f, -3f, -3f, 1f, 9f, -30f });

        Assert.Equal(40, data.Length);
        Assert.Equal(4f, BinaryPrimitives.ReadSingleLittleEndian(data));
        var back = LogitechSpeakerProtocol.DecodeFloats(data, LogitechSpeakerProtocol.BandCount);
        Assert.Equal(new[] { 4f, 3f, 2f, 0f, -4f, -3f, -3f, 1f, 6f, -20f }, back);
    }

    [Fact]
    public void Bands_MustBeTen()
    {
        Assert.Throws<ArgumentException>(() => LogitechSpeakerProtocol.EncodeBands(new float[9]));
    }

    [Fact]
    public void ChannelGains_AreEightLinearGainsClampedToZeroTwo()
    {
        var data = LogitechSpeakerProtocol.EncodeChannelGains(new[] { 1f, 1f, 1.1f, 5f, -1f, 1f, float.NaN, 0.5f });

        Assert.Equal(32, data.Length);
        Assert.Equal(new[] { 1f, 1f, 1.1f, 2f, 0f, 1f, 0f, 0.5f }, LogitechSpeakerProtocol.DecodeFloats(data, LogitechSpeakerProtocol.ChannelCount));
    }

    [Fact]
    public void Decode_RejectsTheWrongLength()
    {
        Assert.Null(LogitechSpeakerProtocol.DecodeFloats(new byte[36], LogitechSpeakerProtocol.BandCount));
        Assert.Null(LogitechSpeakerProtocol.DecodeFloats(null, LogitechSpeakerProtocol.BandCount));
    }

    [Fact]
    public void BuiltInPresets_HaveTenBands_WithinTheSliderRange()
    {
        Assert.NotEmpty(LogitechSpeakerProtocol.BuiltInPresets);
        foreach (var (_, bands) in LogitechSpeakerProtocol.BuiltInPresets)
        {
            Assert.Equal(LogitechSpeakerProtocol.BandCount, bands.Length);
            Assert.All(bands, b => Assert.InRange(b, LogitechSpeakerProtocol.BandMinDb, LogitechSpeakerProtocol.BandMaxDb));
        }
    }

    [Fact]
    public void Hidpp_Request_IsALongReportToTheWiredDevice()
    {
        var request = Hidpp20.Request(0x0B, 1, new byte[] { 70 });

        Assert.Equal(20, request.Length);
        Assert.Equal(new byte[] { 0x11, 0xFF, 0x0B, 0x1A, 70, 0 }, request[..6]);
        Assert.Equal(new byte[] { 0x11, 0xFF, 0x00, 0x0A, 0x83, 0x05 }, Hidpp20.GetFeatureRequest(0x8305)[..6]);
    }

    [Fact]
    public void Hidpp_Classify_TellsOurAnswerFromErrorsAndOpenRgbTraffic()
    {
        var request = Hidpp20.Request(0x0B, 0, ReadOnlySpan<byte>.Empty);

        var answer = new byte[20];
        answer[0] = 0x11; answer[1] = 0xFF; answer[2] = 0x0B; answer[3] = 0x0A; answer[4] = 85;
        Assert.Equal(Hidpp20.ReplyKind.Answer, Hidpp20.Classify(request, answer));

        var error = new byte[20];
        error[0] = 0x11; error[1] = 0xFF; error[2] = 0xFF; error[3] = 0x0B; error[4] = 0x0A; error[5] = 0x07;
        Assert.Equal(Hidpp20.ReplyKind.Error, Hidpp20.Classify(request, error));

        // OpenRGB's software id is 7: the same feature and function is still not ours.
        var openRgb = (byte[])answer.Clone();
        openRgb[3] = 0x07;
        Assert.Equal(Hidpp20.ReplyKind.Unrelated, Hidpp20.Classify(request, openRgb));

        Assert.Equal(Hidpp20.ReplyKind.Unrelated, Hidpp20.Classify(request, new byte[] { 0x02, 0x00, 0x00 }));
    }
}
