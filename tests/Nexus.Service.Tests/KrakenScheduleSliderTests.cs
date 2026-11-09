using System;
using System.Collections.Generic;
using System.Linq;
using Nexus.Service.Peripherals.Hid;
using Nexus.Service.Peripherals.Nzxt;
using Xunit;

namespace Nexus.Service.Tests;

/// <summary>The Kraken reads back the capped level at connect; the remembered slider must stay the user's value.</summary>
public class KrakenScheduleSliderTests
{
    private static readonly KrakenModel EliteNoRgb = KrakenModel.Find(0x300C)!;

    private sealed class Rig
    {
        public readonly KrakenHub Hub = new();
        public int? Remembered;
        public bool Follows = true;
        public double Cap = 1.0;

        public Rig()
        {
            Hub.BindSchedule(
                slider => Math.Min(slider, (int)Math.Round(Cap * 100)),
                () => Remembered,
                level => Remembered = level,
                () => Follows);
        }

        public Recorder Connect(int reading)
        {
            var hid = new Recorder { InfoBrightness = reading };
            Hub.Detach();
            Hub.Attach(hid, EliteNoRgb, KrakenProtocol.ReportLength);
            Assert.True(Hub.Connect());
            return hid;
        }
    }

    [Fact]
    public void A_remembered_slider_survives_a_reconnect_that_reads_back_the_night_cap()
    {
        var rig = new Rig { Remembered = 80, Cap = 1.0 };

        var hid = rig.Connect(reading: 30);

        Assert.Equal(80, rig.Hub.Snapshot.LcdBrightness);
        Assert.Equal(80, rig.Remembered);
        Assert.True(hid.Wrote(80), "the stale capped level on the glass is put back to the slider");
    }

    [Fact]
    public void Without_a_follow_only_an_exact_scaled_match_maps_the_reading_back()
    {
        var rig = new Rig { Remembered = 80, Follows = false, Cap = 0.3 };

        rig.Connect(reading: 55);

        Assert.Equal(55, rig.Hub.Snapshot.LcdBrightness);
    }

    [Fact]
    public async System.Threading.Tasks.Task The_surface_records_the_snapshot_as_the_slider_before_any_cap()
    {
        var rig = new Rig { Remembered = null };
        rig.Connect(reading: 70);
        var surface = new Nexus.Service.Lighting.ScheduleFollow.KrakenScheduleSurface(rig.Hub);

        Assert.Null(surface.Slider("nzxt-kraken-lcd"));
        var hardware = await surface.ReadHardwareAsync("nzxt-kraken-lcd", default);
        surface.RememberSlider("nzxt-kraken-lcd", hardware!.Value);

        Assert.Equal(70, surface.Slider("nzxt-kraken-lcd"));
    }

    private sealed class Recorder : IHidDevice
    {
        private readonly Queue<byte[]> _replies = new();
        public List<byte[]> Writes { get; } = new();
        public int InfoBrightness { get; set; }
        public int VendorId => 0x1E71;
        public int ProductId => 0x300C;
        public string Path => "fake";
        public string? Serial => "TEST";
        public int UsagePage => 0xFF00;
        public int Usage => 1;

        public bool Wrote(int brightness) =>
            Writes.Any(w => w.AsSpan(0, KrakenProtocol.ReportLength).SequenceEqual(KrakenProtocol.EncodeSetBacklight(brightness, 0)));

        public bool Write(ReadOnlySpan<byte> report)
        {
            var copy = report.ToArray();
            Writes.Add(copy);
            var reply = new byte[KrakenProtocol.ReportLength];
            reply[0] = (byte)(copy[0] + 1);
            reply[1] = copy[1];
            reply[14] = KrakenProtocol.AckOk;
            if (copy[0] == 0x30 && copy[1] == 0x01)
            {
                reply[24] = (byte)InfoBrightness;
            }
            _replies.Enqueue(reply);
            return true;
        }

        public int Read(Span<byte> buffer, int timeoutMs)
        {
            if (_replies.Count == 0) return 0;
            var r = _replies.Dequeue();
            r.CopyTo(buffer);
            return r.Length;
        }

        public bool SetFeature(ReadOnlySpan<byte> report) => true;
        public bool GetFeature(Span<byte> buffer) => false;
        public bool GetInputReport(Span<byte> buffer) => false;
        public bool SetOutputReport(ReadOnlySpan<byte> report) => true;
        public void Dispose() { }
    }
}
