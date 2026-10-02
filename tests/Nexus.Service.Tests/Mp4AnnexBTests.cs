using System;
using Nexus.Service.Peripherals.Tryx.Panorama;
using Xunit;

namespace Nexus.Service.Tests;

public class Mp4AnnexBTests
{
    [Fact]
    public void Convert_emits_in_band_parameter_sets_then_annexb_slices()
    {
        // Minimal MP4: an avcC box (1 SPS 67420 00a, 1 PPS 68ce3c80, 4-byte NAL length)
        // and an mdat with one length-prefixed IDR NAL (type 5). No box tree walk needed.
        var mp4 = Convert.FromHexString(
            "0000001b" + "61766343" +                    // avcC box header
            "014d4029" + "ff" + "e1" + "0004" + "6742000a" + // ver/profile/.../numSPS=1, SPS
            "01" + "0004" + "68ce3c80" +                  // numPPS=1, PPS
            "00000011" + "6d646174" +                     // mdat box header
            "00000005" + "6588840001");                   // one 5-byte IDR NAL

        var annexb = Mp4AnnexB.Convert(mp4);

        // Starts with SPS in Annex-B framing.
        Assert.Equal(new byte[] { 0x00, 0x00, 0x00, 0x01, 0x67, 0x42, 0x00, 0x0a }, annexb[..8]);
        // The IDR NAL is present, start-code framed.
        var idr = new byte[] { 0x00, 0x00, 0x00, 0x01, 0x65, 0x88, 0x84, 0x00, 0x01 };
        Assert.True(IndexOf(annexb, idr) >= 0, "IDR slice not found in Annex-B output");
        // PPS present before the IDR (re-emitted per GOP).
        Assert.True(IndexOf(annexb, new byte[] { 0x00, 0x00, 0x00, 0x01, 0x68, 0xce, 0x3c, 0x80 }) >= 0);
    }

    [Fact]
    public void Convert_passes_in_band_headers_through_minus_the_leading_extradata_sei()
    {
        // mdat as x264 repeat-headers + aud writes it through the mp4 muxer: the extradata SEI
        // ffmpeg prepends, then AUD, SPS, PPS, SEI, IDR, and a second AUD + P slice.
        var mp4 = Convert.FromHexString(
            "0000001b" + "61766343" +
            "014d4029" + "ff" + "e1" + "0004" + "6742000a" +
            "01" + "0004" + "68ce3c80" +
            "0000003e" + "6d646174" +
            "00000002" + "0601" +
            "00000002" + "0910" +
            "00000004" + "6742000a" +
            "00000004" + "68ce3c80" +
            "00000002" + "0602" +
            "00000003" + "658884" +
            "00000002" + "0930" +
            "00000003" + "419a00");

        var annexb = Mp4AnnexB.Convert(mp4);

        Assert.Equal(Convert.FromHexString(
            "000000010910" + "000000016742000a" + "0000000168ce3c80" + "000000010602" +
            "00000001658884" + "000000010930" + "00000001419a00"), annexb);
    }

    private static int IndexOf(byte[] hay, byte[] needle)
    {
        for (var i = 0; i + needle.Length <= hay.Length; i++)
        {
            var ok = true;
            for (var j = 0; j < needle.Length; j++)
            {
                if (hay[i + j] != needle[j]) { ok = false; break; }
            }
            if (ok) return i;
        }
        return -1;
    }
}
