using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Linq;
using Nexus.Service.Peripherals.Tryx.Panorama;
using Xunit;

namespace Nexus.Service.Tests;

public class TryxMediaHeadTests
{
    private static readonly byte[] Sps = [0x67, 0x4D, 0x40, 0x32, 0xAA];
    private static readonly byte[] Pps = [0x68, 0xEB, 0xCC];
    // Slice headers: 0x80 in byte 1 = first_mb_in_slice 0 (new picture); 0x40 = a later slice.
    private static readonly byte[] IdrFirst = [0x65, 0x88, 0x11, 0x22];
    private static readonly byte[] IdrSecond = [0x65, 0x40, 0x33];
    private static readonly byte[] PFrame = [0x41, 0x9A, 0x44];

    private static byte[] AnnexB(params byte[][] nals)
        => nals.SelectMany(n => new byte[] { 0, 0, 0, 1 }.Concat(n)).ToArray();

    [Fact]
    public void FindFirstKeyframe_collects_the_parameter_sets_and_idr_slices()
    {
        var es = AnnexB([0x09, 0x10], Sps, Pps, [0x06, 0x05], IdrFirst, IdrSecond, PFrame);

        var kf = TryxMediaHead.FindFirstKeyframe(es);

        Assert.NotNull(kf);
        Assert.Equal(Sps, kf.Sps);
        Assert.Equal(Pps, kf.Pps);
        Assert.Equal([IdrFirst, IdrSecond], kf.Slices);
    }

    [Fact]
    public void FindFirstKeyframe_waits_until_the_idr_is_terminated()
    {
        Assert.Null(TryxMediaHead.FindFirstKeyframe(AnnexB(Sps, Pps, IdrFirst)));
    }

    [Fact]
    public void FindFirstKeyframe_stops_at_the_next_idr_picture()
    {
        var kf = TryxMediaHead.FindFirstKeyframe(AnnexB(Sps, Pps, IdrFirst, IdrFirst, PFrame));

        Assert.NotNull(kf);
        Assert.Single(kf.Slices);
    }

    [Fact]
    public void Parse_reads_bare_annexb_with_the_panel_default_size()
    {
        var head = TryxMediaHead.Parse(AnnexB(Sps, Pps, IdrFirst, PFrame));

        Assert.NotNull(head);
        Assert.Equal((2240, 1080, 0d), (head.Width, head.Height, head.DurationSec));
    }

    [Fact]
    public void Parse_skips_the_container_header_and_derives_the_duration()
    {
        var es = AnnexB(Sps, Pps, IdrFirst, PFrame);
        var container = TryxRkProtocol.WrapMediaContainer(es, fps: 60, width: 1280, height: 640, frameCount: 870, id: 5);

        var head = TryxMediaHead.Parse(container);

        Assert.NotNull(head);
        Assert.Equal((1280, 640, 14.5), (head.Width, head.Height, head.DurationSec));
        Assert.Equal([IdrFirst], head.Keyframe.Slices);
    }

    [Fact]
    public void BuildMatroska_carries_the_avcc_and_length_prefixed_slices()
    {
        var kf = new TryxMediaHead.Keyframe(Sps, Pps, [IdrFirst, IdrSecond]);

        var mkv = TryxMediaHead.BuildMatroska(kf, 2240, 1080);

        Assert.Equal(new byte[] { 0x1A, 0x45, 0xDF, 0xA3 }, mkv[..4]);
        byte[] avcC = [1, 0x4D, 0x40, 0x32, 0xFF, 0xE1, 0, 5, .. Sps, 1, 0, 3, .. Pps];
        Assert.True(mkv.AsSpan().IndexOf(avcC) > 0);
        byte[] frame = [0, 0, 0, 4, .. IdrFirst, 0, 0, 0, 3, .. IdrSecond];
        Assert.True(mkv.AsSpan().EndsWith(frame));
    }
}
