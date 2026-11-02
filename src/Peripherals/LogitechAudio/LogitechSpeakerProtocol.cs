using System;
using System.Buffers.Binary;
using System.Collections.Generic;

namespace Nexus.Service.Peripherals.LogitechAudio;

/// <summary>
/// What G HUB tells Logitech's speaker audio effect (the APO its driver installs on a
/// G560's output, logi_audio_speaker_render_apo.dll) and the speaker itself. The effect
/// reads its settings from the registry, one key per audio endpoint, and reloads them
/// when one of a few endpoint properties is written (it watches them as an
/// IMMNotificationClient). Only the subwoofer level lives in the speaker, behind HID++
/// 2.0 feature 0x8305.
/// </summary>
public static class LogitechSpeakerProtocol
{
    /// <summary>HKLM subkey holding one subkey per endpoint id ("{0.0.0.00000000}.{guid}", lower case).</summary>
    public const string SettingsKey = @"SOFTWARE\Logitech\GHUB\Audio Devices";

    /// <summary>Logitech's speaker effect, as the endpoint's stream and pre-mix effect.</summary>
    public static readonly Guid SpeakerApoClsid = new("8877CBBD-D0D9-441D-AEF8-9B3CC8B88292");

    public const string SelectedEq = "SelectedEQ";
    public const string AdvancedEqParams = "AdvancedEqParams";
    public const string BasicEqParams = "BasicEqParams";
    public const string SelectedSurround = "SelectedSurround";
    public const string DtsOn = "DTSon";
    public const string DtsRoomModel = "DTSroomModel";
    public const string DtsStereoModel = "DTSstereoModel";
    public const string VolumeSpeakers = "VolumeSpeakers";

    /// <summary>SelectedEQ: the ten-band EQ in AdvancedEqParams applies.</summary>
    public const int EqAdvanced = 1;
    /// <summary>SelectedEQ: no user EQ (G HUB's "Enable Advanced EQ" unticked).</summary>
    public const int EqOff = 0;
    /// <summary>SelectedSurround: DTS (G HUB's surround mode enum, DOLBY = 1, DTS = 2).</summary>
    public const int SurroundDts = 2;

    public const int BandCount = 10;
    public const int ChannelCount = 8;

    /// <summary>Centre of each EQ band in Hz, as G HUB labels them for the G560.</summary>
    public static readonly IReadOnlyList<int> BandHz = new[] { 48, 64, 125, 250, 500, 1000, 2000, 4000, 8000, 16000 };

    /// <summary>The range G HUB's sliders offer, in dB.</summary>
    public const float BandMinDb = -20f;
    public const float BandMaxDb = 6f;

    /// <summary>Channel gains are linear (1 = unchanged). G HUB shows ten times the gain ("VOL 11").</summary>
    public const float ChannelMinGain = 0f;
    public const float ChannelMaxGain = 2f;

    /// <summary>Speaker order of VolumeSpeakers: the 7.1 channel order the endpoint plays.</summary>
    public static readonly IReadOnlyList<string> Channels = new[]
    {
        "frontLeft", "frontRight", "center", "subwoofer", "rearLeft", "rearRight", "sideLeft", "sideRight",
    };

    public static class Rooms
    {
        public const int Gaming = 1;
        public const int Movie = 2;
    }

    // Endpoint property keys whose change makes the effect reload a setting. G HUB writes
    // them as VT_UI4 0; the value is ignored, the change notification is the signal.
    public static readonly Guid EqKeys = new("4C4338DA-9899-4709-A311-AB9CAC0EA7B6");
    public static readonly Guid DeviceKeys = new("D57F93AE-4210-46B3-ACFC-90437B666152");
    public static readonly Guid VolumeKeys = new("A9BC0BDC-1C92-4EB3-BFD6-E91E27249850");

    /// <summary>EqKeys pid of a band (0-9) of the ten-band EQ.</summary>
    public static uint AdvancedBandPid(int band) => (uint)band;
    public const uint EqTypePid = 100;
    public const uint SurroundTypePid = 1;
    public const uint DtsStatePid = 20;
    public const uint DtsRoomPid = 21;
    public const uint DtsStereoPid = 22;
    public const uint SpeakerVolumePid = 1;

    /// <summary>Clamps and packs ten band gains (dB) the way AdvancedEqParams holds them: ten little-endian float32.</summary>
    public static byte[] EncodeBands(IReadOnlyList<float> bands)
    {
        if (bands.Count != BandCount)
        {
            throw new ArgumentException($"expected {BandCount} bands, got {bands.Count}", nameof(bands));
        }
        return EncodeFloats(bands, BandMinDb, BandMaxDb);
    }

    /// <summary>Packs the eight channel gains of VolumeSpeakers (linear, clamped).</summary>
    public static byte[] EncodeChannelGains(IReadOnlyList<float> gains)
    {
        if (gains.Count != ChannelCount)
        {
            throw new ArgumentException($"expected {ChannelCount} channels, got {gains.Count}", nameof(gains));
        }
        return EncodeFloats(gains, ChannelMinGain, ChannelMaxGain);
    }

    /// <summary>Reads a float array value; null when it is missing or not exactly <paramref name="count"/> floats.</summary>
    public static float[]? DecodeFloats(byte[]? data, int count)
    {
        if (data is null || data.Length != count * 4)
        {
            return null;
        }
        var values = new float[count];
        for (var i = 0; i < count; i++)
        {
            var value = BinaryPrimitives.ReadSingleLittleEndian(data.AsSpan(i * 4));
            values[i] = float.IsFinite(value) ? value : 0f;
        }
        return values;
    }

    private static byte[] EncodeFloats(IReadOnlyList<float> values, float min, float max)
    {
        var data = new byte[values.Count * 4];
        for (var i = 0; i < values.Count; i++)
        {
            var value = float.IsFinite(values[i]) ? Math.Clamp(values[i], min, max) : 0f;
            BinaryPrimitives.WriteSingleLittleEndian(data.AsSpan(i * 4), value);
        }
        return data;
    }

    /// <summary>
    /// The presets G HUB ships for the G560 (its device_presets.json, dB per band, <see cref="BandHz"/> order).
    /// Apart from Communications they only cut, unlike the general presets G HUB has for other devices.
    /// </summary>
    public static readonly IReadOnlyList<(string Name, float[] Bands)> BuiltInPresets = new (string, float[])[]
    {
        ("Bass Boost", new float[] { 0, 0, 0, 0, 0, -3, -4, -5, -6, -6 }),
        ("Cinematic", new float[] { -3, -4, -5, -6, -6, -5, -6, 0, 0, 0 }),
        ("Communications", new[] { 0, -0.96f, -0.96f, 1.92f, 3.6f, 4.32f, 3.6f, 3.6f, 1.68f, 0.96f }),
        ("Flat", new float[] { 0, 0, 0, 0, 0, 0, 0, 0, 0, 0 }),
        ("First Person Shooter (FPS)", new float[] { 0, 0, -6, -5, -3, -2, -3, -3, -5, -6 }),
        ("Multiplayer Online Battle Arena (MOBA)", new float[] { 0, -10, -11, 0, -2, -5, -3, -17, -3, 0 }),
    };
}

/// <summary>
/// HID++ 2.0 long reports (report id 0x11, 20 bytes) on a wired Logitech device
/// (device index 0xFF), as far as the subwoofer level needs them.
/// </summary>
public static class Hidpp20
{
    public const byte LongReportId = 0x11;
    public const int LongReportLength = 20;
    public const byte WiredDeviceIndex = 0xFF;
    /// <summary>Our software id (low nibble of byte 3). OpenRGB uses 0x07, so replies to either never mix.</summary>
    public const byte SoftwareId = 0x0A;
    public const ushort RootFeature = 0x0000;
    public const ushort BassToneFeature = 0x8305;
    public const byte ErrorFeatureIndex = 0xFF;

    /// <summary>A request: feature index, function (0-15) and up to 16 parameter bytes.</summary>
    public static byte[] Request(byte featureIndex, byte function, ReadOnlySpan<byte> parameters)
    {
        if (function > 0x0F)
        {
            throw new ArgumentOutOfRangeException(nameof(function));
        }
        if (parameters.Length > LongReportLength - 4)
        {
            throw new ArgumentOutOfRangeException(nameof(parameters));
        }
        var report = new byte[LongReportLength];
        report[0] = LongReportId;
        report[1] = WiredDeviceIndex;
        report[2] = featureIndex;
        report[3] = (byte)((function << 4) | SoftwareId);
        parameters.CopyTo(report.AsSpan(4));
        return report;
    }

    /// <summary>Root.GetFeature(id): the reply's first parameter is the feature's index, 0 when absent.</summary>
    public static byte[] GetFeatureRequest(ushort featureId) =>
        Request(0x00, 0, new[] { (byte)(featureId >> 8), (byte)featureId });

    public enum ReplyKind
    {
        /// <summary>Not an answer to this request (another app's traffic, input reports).</summary>
        Unrelated,
        Answer,
        Error,
    }

    /// <summary>Whether <paramref name="report"/> answers <paramref name="request"/>, and with what.</summary>
    public static ReplyKind Classify(ReadOnlySpan<byte> request, ReadOnlySpan<byte> report)
    {
        if (report.Length < 7 || (report[0] != LongReportId && report[0] != 0x10) || report[1] != request[1])
        {
            return ReplyKind.Unrelated;
        }
        if (report[2] == request[2] && report[3] == request[3])
        {
            return ReplyKind.Answer;
        }
        if (report[2] == ErrorFeatureIndex && report[3] == request[2] && report[4] == request[3])
        {
            return ReplyKind.Error;
        }
        return ReplyKind.Unrelated;
    }
}
