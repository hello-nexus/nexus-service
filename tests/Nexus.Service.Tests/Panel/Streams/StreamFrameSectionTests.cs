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
        session.OfferSharedFrames("Global\\n", 10);
        session.ReplaceSection(section, session.ResetForNewIngest());
        session.Close();
        session.Close();
        Assert.Equal(1, section.Disposed);
        Assert.Equal("Global\\n", session.FrameSectionName);
    }

    [Fact]
    public void ReplaceSection_disposes_the_previous_section_and_a_late_one_after_close()
    {
        var session = new StreamSession(
            "s",
            new StreamedPanelDeviceInfo
            {
                Serial = "x",
                Profile = new StreamedPanelProfile { Kind = "t", DisplayName = "t", Surface = "monitor", CssWidth = 64, CssHeight = 64, Fps = 30 },
            },
            "p");
        var first = new CountingDisposable();
        var second = new CountingDisposable();
        var late = new CountingDisposable();
        var generation = session.ResetForNewIngest();
        session.ReplaceSection(first, generation);
        session.ReplaceSection(second, generation);
        Assert.Equal(1, first.Disposed);
        Assert.Equal(0, second.Disposed);
        session.Close();
        Assert.Equal(1, second.Disposed);
        session.ReplaceSection(late, generation);
        Assert.Equal(1, late.Disposed);
    }

    [Fact]
    public void A_section_from_a_superseded_bind_is_disposed_not_installed()
    {
        var session = new StreamSession(
            "s",
            new StreamedPanelDeviceInfo
            {
                Serial = "x",
                Profile = new StreamedPanelProfile { Kind = "t", DisplayName = "t", Surface = "monitor", CssWidth = 64, CssHeight = 64, Fps = 30 },
            },
            "p");
        var slowA = new CountingDisposable();
        var fastB = new CountingDisposable();
        var genA = session.ResetForNewIngest();
        var genB = session.ResetForNewIngest();
        session.ReplaceSection(fastB, genB);
        session.ReplaceSection(slowA, genA);
        Assert.Equal(1, slowA.Disposed);
        Assert.Equal(0, fastB.Disposed);
        session.Close();
        Assert.Equal(1, fastB.Disposed);
        Assert.Equal(1, slowA.Disposed);
    }

    [Fact]
    public void A_reader_never_accepts_a_frame_the_writer_overwrote_mid_copy()
    {
        const int Bytes = 1 << 16;
        var section = new byte[StreamFrameSection.HeaderBytes + Bytes];
        fixed (byte* p = section)
        {
            StreamFrameSection.Initialize(p);
        }
        using var stop = new System.Threading.CancellationTokenSource();
        var writer = new System.Threading.Thread(() =>
        {
            fixed (byte* p = section)
            {
                long sequence = 0;
                byte fill = 1;
                while (!stop.IsCancellationRequested)
                {
                    System.Threading.Volatile.Write(ref *(long*)(p + 8), sequence + 1);
                    *(int*)(p + 28) = Bytes;
                    new Span<byte>(p + StreamFrameSection.HeaderBytes, Bytes).Fill(fill);
                    System.Threading.Volatile.Write(ref *(long*)(p + 8), sequence + 2);
                    sequence += 2;
                    fill = fill == 1 ? (byte)2 : (byte)1;
                }
            }
        });
        writer.Start();
        var accepted = 0;
        var destination = new byte[Bytes];
        long last = 0;
        var deadline = Environment.TickCount64 + 400;
        while (Environment.TickCount64 < deadline)
        {
            if (Read(section, ref last, destination, out var length))
            {
                accepted++;
                var first = destination[0];
                Assert.Equal(Bytes, length);
                Assert.True(destination.AsSpan(0, length).IndexOfAnyExcept(first) < 0, "accepted a frame mixing two writes");
            }
        }
        stop.Cancel();
        writer.Join();
        Assert.True(accepted > 0);
    }

    [Fact]
    public void HasNew_follows_the_sequence()
    {
        var section = NewSection();
        long last = 0;
        fixed (byte* p = section)
        {
            Assert.False(StreamFrameSection.HasNew(p, last));
        }
        Write(section, 2, 1);
        fixed (byte* p = section)
        {
            Assert.True(StreamFrameSection.HasNew(p, last));
            Assert.False(StreamFrameSection.HasNew(p, 2));
        }
        Write(section, 3, 1);
        fixed (byte* p = section)
        {
            Assert.False(StreamFrameSection.HasNew(p, 2));
        }
    }

    [Fact]
    public void PublishConsumed_lands_in_the_header()
    {
        var section = NewSection();
        fixed (byte* p = section)
        {
            StreamFrameSection.PublishConsumed(p, 42);
        }
        Assert.Equal(42, BinaryPrimitives.ReadInt64LittleEndian(section.AsSpan(32)));
    }

    [Theory]
    [InlineData("0123456789abcdef", true)]
    [InlineData("0123456789ABCDEF", false)]
    [InlineData("0123456789abcde", false)]
    [InlineData("0123456789abcdeg", false)]
    [InlineData("..\\x-0123456789abcd", false)]
    [InlineData(null, false)]
    public void Writer_nonce_is_sixteen_lowercase_hex_digits(string? nonce, bool valid)
    {
        Assert.Equal(valid, StreamFrameSection.IsValidNonce(nonce));
    }

    [Fact]
    public void Section_and_event_names_carry_the_nonce()
    {
        var name = StreamFrameSection.SectionName("Global\\NexusPanelStream-abc", "0123456789abcdef");
        Assert.Equal("Global\\NexusPanelStream-abc-0123456789abcdef", name);
        Assert.Equal(name + "-Ready", StreamFrameSection.ReadyName(name));
    }

    [Theory]
    [InlineData("S-1-5-21-1004336348-1177238915-682003330-1001", true)]
    [InlineData("S-1-5-18", true)]
    [InlineData("S-1-5-21-1)(A;;GA;;;WD", false)]
    [InlineData("S-1-5", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void Sddl_names_only_system_and_the_given_user(string? sid, bool valid)
    {
        var sddl = StreamFrameSection.Sddl(sid);
        if (!valid)
        {
            Assert.Null(sddl);
            return;
        }
        Assert.Equal($"D:P(A;;GA;;;SY)(A;;GRGW;;;{sid})", sddl);
        Assert.DoesNotContain(";IU)", sddl);
        Assert.DoesNotContain(";WD)", sddl);
    }

    private sealed class CountingDisposable : IDisposable
    {
        public int Disposed;
        public void Dispose() => Disposed++;
    }
}
