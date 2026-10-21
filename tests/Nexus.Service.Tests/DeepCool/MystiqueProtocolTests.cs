using System;
using Nexus.Service.Peripherals.DeepCool;
using Nexus.Service.Persistence;
using Xunit;

namespace Nexus.Service.Tests.DeepCool;

public class MystiqueProtocolTests
{
    // Frames below are byte-for-byte the traffic of a recorded session.
    private const string Info = "AA2E12000000000000000000000000000000000000000000000000000000000000000000000000000000484944430202";
    private const string SystemStart = "AA2E02010102004600000000000000000000000000000000000000000000000000000000000000000000484944433C02";
    private const string SystemDim = "AA2E02010102001500000000000000000000000000000000000000000000000000000000000000000000484944430B02";
    private const string SystemDark = "AA2E0201010200000000000000000000000000000000000000000000000000000000000000000000000048494443F601";
    private const string SystemSyncOn = "AA2E0201010201000000000000000000000000000000000000000000000000000000000000000000000048494443F701";
    private const string SystemGyroOff = "AA2E0201000201000000000000000000000000000000000000000000000000000000000000000000000048494443F601";
    private const string RecordApply = "AA2E0601000000000000000000000000000000000000000000000000000000000000000000000000000048494443F701";
    private const string Clock = "AA2E0AEA070A15101A3B0000000000000000000000000000000000000000000000000000000000000000484944436F03";
    private const string Poll = "AA2E10000000000000000000000000000000000000000000000000000000000000000000000000000000484944430002";
    private const string AckSystem = "552E02000000000000000000000000000000000000000000000000000000000000000000000000000000484944439D01";
    private const string InfoReply = "552E12003033464630323130303445303035383439343036394230373030303830303039000000000000484944434F08";

    private static string Hex(byte[] b) => Convert.ToHexString(b);

    [Fact]
    public void Poll_and_info_query_match_the_recorded_frames()
    {
        Assert.Equal(Poll, Hex(MystiqueProtocol.BuildPoll()));
        Assert.Equal(Info, Hex(MystiqueProtocol.BuildInfoQuery()));
    }

    [Fact]
    public void Clock_carries_local_time_with_a_little_endian_year()
    {
        Assert.Equal(Clock, Hex(MystiqueProtocol.BuildClock(new DateTime(2026, 10, 21, 16, 26, 59))));
    }

    [Theory]
    [InlineData(true, false, 70, SystemStart)]
    [InlineData(true, false, 21, SystemDim)]
    [InlineData(true, false, 0, SystemDark)]
    [InlineData(true, true, 0, SystemSyncOn)]
    [InlineData(false, true, 0, SystemGyroOff)]
    public void System_frame_matches_the_recorded_frames(bool gyroscope, bool rgbSync, int brightness, string expected)
    {
        Assert.Equal(expected, Hex(MystiqueProtocol.BuildSystem(gyroscope, rgbSync, brightness)));
    }

    [Theory]
    [InlineData(-5, 0)]
    [InlineData(250, 100)]
    public void System_brightness_is_clamped_to_a_percent(int given, int sent)
    {
        Assert.Equal(sent, MystiqueProtocol.BuildSystem(true, false, given)[7]);
    }

    [Theory]
    [InlineData(DeepCoolMystiqueSettings.ModeMonitor, "AA2E0301000000000000000000000000000000000000000000000000000000000000000000000000000048494443F401")]
    [InlineData(DeepCoolMystiqueSettings.ModeMedia, "AA2E0302000000000000000000000000000000000000000000000000000000000000000000000000000048494443F501")]
    [InlineData(DeepCoolMystiqueSettings.ModeRecording, "AA2E0303000000000000000000000000000000000000000000000000000000000000000000000000000048494443F601")]
    public void Mode_frame_matches_the_recorded_frames(string mode, string expected)
    {
        Assert.Equal(expected, Hex(MystiqueProtocol.BuildMode(mode)));
    }

    [Theory]
    [InlineData(DeepCoolMystiqueSettings.ReadoutFrequency, "AA2E0400000002000000000000000000000000000000000000000000000000000000000000000000000048494443F601")]
    [InlineData(DeepCoolMystiqueSettings.ReadoutTime, "AA2E0401000002000000000000000000000000000000000000000000000000000000000000000000000048494443F701")]
    [InlineData(DeepCoolMystiqueSettings.ReadoutPump, "AA2E0402000002000000000000000000000000000000000000000000000000000000000000000000000048494443F801")]
    [InlineData(DeepCoolMystiqueSettings.ReadoutFan, "AA2E0403000002000000000000000000000000000000000000000000000000000000000000000000000048494443F901")]
    [InlineData(DeepCoolMystiqueSettings.ReadoutFanPump, "AA2E0404000002000000000000000000000000000000000000000000000000000000000000000000000048494443FA01")]
    [InlineData(DeepCoolMystiqueSettings.ReadoutTemperature, "AA2E0405000002000000000000000000000000000000000000000000000000000000000000000000000048494443FB01")]
    public void Readout_frame_matches_the_recorded_frames(string readout, string expected)
    {
        Assert.Equal(expected, Hex(MystiqueProtocol.BuildReadout(readout)));
    }

    [Theory]
    [InlineData(false, true, "AA2E0500010000000000000000000000000000000000000000000000000000000000000000000000000048494443F601")]
    [InlineData(true, false, "AA2E0501000000000000000000000000000000000000000000000000000000000000000000000000000048494443F601")]
    [InlineData(true, true, "AA2E0501010000000000000000000000000000000000000000000000000000000000000000000000000048494443F701")]
    public void Recording_frame_matches_the_recorded_frames(bool frequency, bool temperature, string expected)
    {
        Assert.Equal(expected, Hex(MystiqueProtocol.BuildRecording(frequency, temperature)));
        Assert.Equal(RecordApply, Hex(MystiqueProtocol.BuildRecordingApply()));
    }

    [Theory]
    [InlineData("AA2E0131000002000013000403001304005F0B005F0500269306000D0D0000000000000000000000000048494443FC03", 49, 2, 5380, 1683, 3341)]
    [InlineData("AA2E0131000003000013000403001304005F0B005F0500268F06000D0D0000000000000000000000000048494443F903", 49, 3, 5380, 1679, 3341)]
    [InlineData("AA2E0134000002000013000403001304005F0B005F050026B506000D0D00000000000000000000000000484944432104", 52, 2, 5380, 1717, 3341)]
    [InlineData("AA2E0132000002000013000403001304005F0B005F03003BAC06000D0D00000000000000000000000000484944432904", 50, 2, 3590, 1708, 3341)]
    public void Sensor_push_reproduces_the_recorded_frames(string expected, int temp, int usage, int mhz, int fan, int pump)
    {
        Assert.Equal(expected, Hex(MystiqueProtocol.BuildSensors(new MystiqueReading(temp, usage, mhz, fan, pump))));
    }

    [Fact]
    public void Sensor_push_encodes_each_field_at_its_offset()
    {
        var frame = MystiqueProtocol.BuildSensors(new MystiqueReading(73, 18, 4870, 2327, 3358));

        Assert.Equal(73, frame[3]);
        Assert.Equal(18, frame[6]);
        Assert.Equal(4, frame[21]);
        Assert.Equal(87, frame[23]);
        Assert.Equal(2327, frame[24] | frame[25] << 8 | frame[26] << 16);
        Assert.Equal(3358, frame[27] | frame[28] << 8 | frame[29] << 16);
    }

    [Fact]
    public void Sensor_push_clamps_out_of_range_values()
    {
        var frame = MystiqueProtocol.BuildSensors(new MystiqueReading(-40, 400, -1, -1, int.MaxValue));

        Assert.Equal(0, frame[3]);
        Assert.Equal(100, frame[6]);
        Assert.Equal(0, frame[21]);
        Assert.Equal(0, frame[23]);
        Assert.Equal(0, frame[24] | frame[25] << 8 | frame[26] << 16);
        Assert.Equal(0xFFFFFF, frame[27] | frame[28] << 8 | frame[29] << 16);
    }

    [Fact]
    public void Every_frame_ends_with_the_tag_and_a_little_endian_sum()
    {
        var frame = MystiqueProtocol.BuildSensors(new MystiqueReading(55, 3, 5380, 1700, 3341));
        int sum = 0;
        for (int i = 0; i < 46; i++)
        {
            sum += frame[i];
        }

        Assert.Equal(48, frame.Length);
        Assert.Equal("HIDC", System.Text.Encoding.ASCII.GetString(frame, 42, 4));
        Assert.Equal(sum & 0xFF, frame[46]);
        Assert.Equal(sum >> 8, frame[47]);
    }

    [Fact]
    public void Reply_check_accepts_recorded_replies_and_rejects_damaged_ones()
    {
        var ack = Convert.FromHexString(AckSystem);
        Assert.True(MystiqueProtocol.IsReply(ack, MystiqueProtocol.CmdSystem));
        Assert.True(MystiqueProtocol.IsReply(Convert.FromHexString(InfoReply), MystiqueProtocol.CmdInfo));
        Assert.False(MystiqueProtocol.IsReply(ack, MystiqueProtocol.CmdMode));

        ack[10] ^= 0xFF;
        Assert.False(MystiqueProtocol.IsReply(ack, MystiqueProtocol.CmdSystem));
        Assert.False(MystiqueProtocol.IsReply(new byte[10], MystiqueProtocol.CmdSystem));
    }

    [Fact]
    public void An_oversized_payload_is_refused()
    {
        Assert.Throws<ArgumentException>(() => MystiqueProtocol.BuildFrame(1, new byte[MystiqueProtocol.MaxPayload + 1]));
    }
}
