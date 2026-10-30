using System;
using System.Buffers.Binary;
using Nexus.Service.Panel.Streams;
using Xunit;

namespace Nexus.Service.Tests.Panel.Streams;

public sealed unsafe class StreamFrameSectionTests
{
    private const int FrameBytes = 16;

    // Writer side of the layout, as the overlay's SharedFrameWriter produces it.
    private static void Write(byte[] section, long sequence, byte fill, int bytes = FrameBytes)
    {
        BinaryPrimitives.WriteInt64LittleEndian(section.AsSpan(8), sequence);
        BinaryPrimitives.WriteInt32LittleEndian(section.AsSpan(16), 4);
        BinaryPrimitives.WriteInt32LittleEndian(section.AsSpan(20), bytes / 16);
        BinaryPrimitives.WriteInt32LittleEndian(section.AsSpan(28), bytes);
        section.AsSpan(StreamFrameSection.HeaderBytes, bytes).Fill(fill);
    }

    private static byte[] NewSection()
    {
        var section = new byte[StreamFrameSection.HeaderBytes + FrameBytes];
        fixed (byte* p = section) StreamFrameSection.Initialize(p);
        return section;
    }

    private static bool Read(byte[] section, ref long last, byte[] destination, out int length)
    {
        fixed (byte* p = section)
        {
            return StreamFrameSection.TryRead(p, section.Length, ref last, destination, out length);
        }
    }

    [Fact]
    public void Nothing_to_read_before_the_first_write()
    {
        var section = NewSection();
        long last = 0;
        Assert.False(Read(section, ref last, new byte[FrameBytes], out _));
    }

    [Fact]
    public void Reads_a_new_frame_once()
    {
        var section = NewSection();
        Write(section, 2, 0xAB);
        long last = 0;
        var dest = new byte[FrameBytes];
        Assert.True(Read(section, ref last, dest, out var length));
        Assert.Equal(FrameBytes, length);
        Assert.All(dest, b => Assert.Equal(0xAB, b));
        Assert.False(Read(section, ref last, dest, out _));
        Write(section, 4, 0xCD);
        Assert.True(Read(section, ref last, dest, out _));
        Assert.Equal(0xCD, dest[0]);
    }

    [Fact]
    public void Skips_a_frame_the_writer_is_in_the_middle_of()
    {
        var section = NewSection();
        Write(section, 3, 0x11);
        long last = 0;
        Assert.False(Read(section, ref last, new byte[FrameBytes], out _));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-4)]
    [InlineData(32)]
    public void Rejects_a_frame_that_does_not_fit(int bytes)
    {
        var section = NewSection();
        Write(section, 2, 0x22);
        BinaryPrimitives.WriteInt32LittleEndian(section.AsSpan(28), bytes);
        long last = 0;
        Assert.False(Read(section, ref last, new byte[FrameBytes], out _));
    }

    [Fact]
    public void Rejects_a_section_with_the_wrong_magic()
    {
        var section = NewSection();
        Write(section, 2, 0x33);
        section[0] ^= 0xFF;
        long last = 0;
        Assert.False(Read(section, ref last, new byte[FrameBytes], out _));
    }

    [Fact]
    public void AcceptFrame_queues_the_frame_and_records_the_gap()
    {
        var session = new StreamSession(
            "s",
            new StreamedPanelDeviceInfo
            {
                Serial = "x",
                Profile = new StreamedPanelProfile
                {
                    Kind = "t", DisplayName = "t", Surface = "monitor",
                    CssWidth = 64, CssHeight = 64, Fps = 30, Codec = StreamCodec.RawBgra,
                },
            },
            "p");
        session.AcceptFrame(new StreamFrame { Flags = StreamFraming.FlagIdr, Payload = new byte[4] });
        session.AcceptFrame(new StreamFrame { Flags = StreamFraming.FlagIdr, Payload = new byte[4] });
        Assert.Equal(2, session.QueueDepthForTest);
    }

    [Fact]
    public void Close_disposes_the_attached_section_once()
    {
        var session = new StreamSession(
            "s",
            new StreamedPanelDeviceInfo
            {
                Serial = "x",
                Profile = new StreamedPanelProfile { Kind = "t", DisplayName = "t", Surface = "monitor", CssWidth = 64, CssHeight = 64, Fps = 30 },
            },
            "p");
        var section = new CountingDisposable();
        session.AttachSection(section, "Global\\n", 10);
        session.Close();
        session.Close();
        Assert.Equal(1, section.Disposed);
        Assert.Equal("Global\\n", session.FrameSectionName);
    }

    private sealed class CountingDisposable : IDisposable
    {
        public int Disposed;
        public void Dispose() => Disposed++;
    }
}
