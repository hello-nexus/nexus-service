using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Nexus.Service.Models.Displays;
using Nexus.Service.Platform.Displays;
using Xunit;

namespace Nexus.Service.Tests;

/// <summary>
/// Covers the no-hardware parts of LinuxDisplayBrightnessProvider: the sysfs
/// backlight read/write (fake /sys/class/backlight tree) and the EDID decode.
/// The DDC/CI i2c transaction is hardware-only and not covered here.
/// </summary>
public class LinuxDisplayBrightnessTests
{
    [Fact]
    public void ReadBacklightPercent_ScalesByMax()
    {
        using var t = new TempDir();
        var dir = t.Dir("bl/intel");
        t.Write("bl/intel/max_brightness", "1000\n");
        t.Write("bl/intel/actual_brightness", "500\n");
        Assert.Equal(50, LinuxDisplayBrightnessProvider.ReadBacklightPercent(dir));
    }

    [Fact]
    public void ReadBacklightPercent_FallsBackToBrightnessFile()
    {
        using var t = new TempDir();
        var dir = t.Dir("bl/intel");
        t.Write("bl/intel/max_brightness", "100\n");
        t.Write("bl/intel/brightness", "20\n"); // no actual_brightness
        Assert.Equal(20, LinuxDisplayBrightnessProvider.ReadBacklightPercent(dir));
    }

    [Fact]
    public void WriteBacklightPercent_WritesScaledRaw()
    {
        using var t = new TempDir();
        var dir = t.Dir("bl/intel");
        t.Write("bl/intel/max_brightness", "1000\n");
        t.Write("bl/intel/brightness", "0\n");
        Assert.True(LinuxDisplayBrightnessProvider.WriteBacklightPercent(dir, 75));
        Assert.Equal("750", File.ReadAllText(Path.Combine(dir, "brightness")));
    }

    [Fact]
    public void WriteBacklightPercent_NoMaxFails()
    {
        using var t = new TempDir();
        Assert.False(LinuxDisplayBrightnessProvider.WriteBacklightPercent(t.Dir("bl/empty"), 50));
    }

    [Fact]
    public void DecodeEdid_ParsesManufacturerAndModel()
    {
        var edid = new byte[128];
        edid[0] = 0x00;
        edid[1] = 0xFF;
        // Manufacturer "DEL" -> PNP id 0x10AC (Dell).
        var id = ((4 & 0x1F) << 10) | ((5 & 0x1F) << 5) | (12 & 0x1F); // D,E,L
        edid[8] = (byte)(id >> 8);
        edid[9] = (byte)(id & 0xFF);
        // Model-name descriptor (tag 0xFC) at offset 54: 00 00 00 FC 00 + ASCII + 0x0A.
        edid[57] = 0xFC;
        var name = Encoding.ASCII.GetBytes("U2720Q");
        Array.Copy(name, 0, edid, 59, name.Length);
        edid[59 + name.Length] = 0x0A;

        var (mfg, model) = LinuxDisplayBrightnessProvider.DecodeEdid(edid);
        Assert.Equal("DEL", mfg);
        Assert.Equal("U2720Q", model);
    }

    [Fact]
    public void DecodeEdid_InvalidHeaderReturnsEmpty()
        => Assert.Equal(("", ""), LinuxDisplayBrightnessProvider.DecodeEdid(new byte[128]));

    /// <summary>
    /// The Y70 ships PNP id RTK0004 with model-name descriptor "HYTE Y70ti", so
    /// Y70DisplayProtocol.DdcPanelHardwareNames only matches via the PNP id.
    /// </summary>
    [Fact]
    public void DecodePnpId_ComposesManufacturerAndProductCode()
    {
        var edid = new byte[128];
        edid[0] = 0x00;
        edid[1] = 0xFF;
        var id = ((18 & 0x1F) << 10) | ((20 & 0x1F) << 5) | (11 & 0x1F); // R,T,K
        edid[8] = (byte)(id >> 8);
        edid[9] = (byte)(id & 0xFF);
        edid[10] = 0x04; // product code 0x0004, little-endian
        edid[11] = 0x00;
        edid[57] = 0xFC;
        var name = Encoding.ASCII.GetBytes("HYTE Y70ti");
        Array.Copy(name, 0, edid, 59, name.Length);
        edid[59 + name.Length] = 0x0A;

        Assert.Equal("RTK0004", LinuxDisplayBrightnessProvider.DecodePnpId(edid));
        Assert.Equal("HYTE Y70ti", LinuxDisplayBrightnessProvider.DecodeEdid(edid).Model);
    }

    [Fact]
    public void DecodePnpId_InvalidHeaderReturnsEmpty()
        => Assert.Equal("", LinuxDisplayBrightnessProvider.DecodePnpId(new byte[128]));

    private static void Connector(TempDir t, string name, string status, string? ddcBus = null, string? childBus = null)
    {
        t.Write($"drm/{name}/status", status + "\n");
        if (ddcBus is not null)
            t.Symlink($"drm/{name}/ddc", t.Dir($"devices/{ddcBus}"));
        if (childBus is not null)
            t.Dir($"drm/{name}/{childBus}");
    }

    private static List<int> Primaries(TempDir t, Action<string>? log = null)
        => LinuxDisplayBrightnessProvider.SelectDdcCandidates(t.At("drm"), t.At("devices"), log).SelectMany(g => g.Primary).Distinct().Order().ToList();

    private static List<int> Fallbacks(TempDir t)
        => LinuxDisplayBrightnessProvider.SelectDdcCandidates(t.At("drm"), t.At("devices")).SelectMany(g => g.Fallback).Distinct().Order().ToList();

    [Fact]
    public void SelectDdcCandidates_ReturnsOnlyConnectedConnectorBuses()
    {
        using var t = new TempDir();
        Connector(t, "card1-DP-1", "connected", ddcBus: "i2c-7");
        Connector(t, "card1-DP-2", "disconnected", ddcBus: "i2c-8");
        Connector(t, "card1-HDMI-A-1", "connected", ddcBus: "i2c-3");
        t.Dir("drm/card1"); // GPU node, not a connector
        t.Dir("devices/i2c-0"); // non-NVIDIA buses without a connector are never listed

        Assert.Equal(new[] { 3, 7 }, Primaries(t));
        Assert.Empty(Fallbacks(t));
    }

    [Fact]
    public void SelectDdcCandidates_UsesI2cChildDirWhenThereIsNoDdcLink()
    {
        using var t = new TempDir();
        Connector(t, "card0-DP-1", "connected", childBus: "i2c-5");

        Assert.Equal(new[] { 5 }, Primaries(t));
    }

    [Fact]
    public void SelectDdcCandidates_ConnectedWithoutDdcLinkYieldsNothing()
    {
        using var t = new TempDir();
        Connector(t, "card0-eDP-1", "connected");

        Assert.Empty(Primaries(t));
    }

    [Fact]
    public void SelectDdcCandidates_MissingRootYieldsNothing()
    {
        using var t = new TempDir();

        Assert.Empty(LinuxDisplayBrightnessProvider.SelectDdcCandidates(t.At("absent"), t.At("devices")));
    }

    [Fact]
    public void SelectDdcCandidates_IgnoresNonI2cLinkTarget()
    {
        using var t = new TempDir();
        Connector(t, "card0-DP-1", "connected", ddcBus: "aux-0");

        Assert.Empty(Primaries(t));
    }

    [Fact]
    public void SelectDdcCandidates_SkipsSmuAdapterEvenWhenLinkedFromAConnector()
    {
        using var t = new TempDir();
        t.Write("devices/i2c-4/name", "AMDGPU SMU 4\n");
        t.Write("devices/i2c-7/name", "AMDGPU DM i2c hw bus 1\n");
        Connector(t, "card1-DP-1", "connected", ddcBus: "i2c-4");
        Connector(t, "card1-DP-2", "connected", ddcBus: "i2c-7");
        t.Write("drm/card1-HDMI-A-1/i2c-9/name", "AMDGPU SMU 9\n");
        Connector(t, "card1-HDMI-A-1", "connected", childBus: "i2c-9");

        Assert.Equal(new[] { 7 }, Primaries(t));
        Assert.Empty(Fallbacks(t));
    }

    [Fact]
    public void SelectDdcCandidates_LogsSelectionAndRejectionReasons()
    {
        using var t = new TempDir();
        t.Write("drm/card1-DP-2/i2c-5/name", "AMDGPU DM aux hw bus 1\n");
        Connector(t, "card1-DP-2", "connected", childBus: "i2c-5");
        t.Write("drm/card1-DP-1/i2c-4/name", "AMDGPU SMU 4\n");
        Connector(t, "card1-DP-1", "connected", childBus: "i2c-4");
        Connector(t, "card1-HDMI-A-1", "disconnected");
        Connector(t, "card1-DP-3", "connected");
        var lines = new List<string>();

        var buses = Primaries(t, lines.Add);

        Assert.Equal(new[] { 5 }, buses);
        Assert.Contains(lines, l => l.Contains("card1-DP-2") && l.Contains("i2c-5 selected") && l.Contains("AMDGPU DM aux hw bus 1"));
        Assert.Contains(lines, l => l.Contains("card1-DP-1") && l.Contains("SMU adapter"));
        Assert.Contains(lines, l => l.Contains("card1-HDMI-A-1 skipped") && l.Contains("disconnected"));
        Assert.Contains(lines, l => l.Contains("card1-DP-3") && l.Contains("no i2c child and no ddc link"));
    }

    [Fact]
    public void SelectDdcCandidates_UnionsNvidiaAdaptersWithConnectorBuses()
    {
        using var t = new TempDir();
        t.Write("devices/i2c-2/name", "NVIDIA i2c adapter 0 at 1:00.0\n");
        t.Write("devices/i2c-3/name", "NVIDIA i2c adapter 1 at 1:00.0\n");
        t.Write("devices/i2c-4/name", "AMDGPU SMU 4\n");
        t.Write("devices/i2c-6/name", "SMBus I801 adapter\n");
        t.Write("devices/i2c-7/name", "AMDGPU DM i2c hw bus 1\n");
        Connector(t, "card0-DP-1", "connected", ddcBus: "i2c-7");
        Connector(t, "card0-DP-2", "connected", ddcBus: "i2c-3");

        Assert.Equal(new[] { 2, 3, 7 }, Primaries(t));
        Assert.Empty(Fallbacks(t));

        var groups = LinuxDisplayBrightnessProvider.SelectDdcCandidates(t.At("drm"), t.At("devices"));
        Assert.Contains(groups, g => !g.OnConnector && g.Primary.SequenceEqual(new[] { 2 }));
        Assert.Contains(groups, g => g.OnConnector && g.Primary.Contains(7));
    }

    [Fact]
    public void SelectDdcCandidates_AmdDpPrefersAuxChildOverDdcLink_AndNeverSelectsSmu()
    {
        using var t = new TempDir();
        t.Write("devices/i2c-4/name", "AMDGPU DM i2c hw bus 0\n");
        t.Write("devices/i2c-5/name", "AMDGPU DM i2c hw bus 1\n");
        t.Write("devices/i2c-6/name", "AMDGPU SMU 0\n");
        t.Write("devices/i2c-7/name", "AMDGPU SMU 1\n");
        t.Write("drm/card1-DP-1/i2c-9/name", "AMDGPU DM aux hw bus 0\n");
        t.Write("drm/card1-DP-2/i2c-10/name", "AMDGPU DM aux hw bus 1\n");
        Connector(t, "card1-DP-1", "connected", ddcBus: "i2c-4", childBus: "i2c-9");
        Connector(t, "card1-DP-2", "connected", ddcBus: "i2c-5", childBus: "i2c-10");
        Connector(t, "card1-HDMI-A-1", "disconnected", ddcBus: "i2c-6");

        var buses = Primaries(t);

        Assert.Equal(new[] { 9, 10 }, buses);
        Assert.Equal(new[] { 4, 5 }, Fallbacks(t));
        Assert.DoesNotContain(6, buses);
        Assert.DoesNotContain(7, buses);
    }

    [Fact]
    public void SelectDdcCandidates_SmuAuxChildIsNeverSelected()
    {
        using var t = new TempDir();
        t.Write("devices/i2c-4/name", "AMDGPU DM i2c hw bus 0\n");
        t.Write("drm/card1-DP-1/i2c-6/name", "AMDGPU SMU 0\n");
        Connector(t, "card1-DP-1", "connected", ddcBus: "i2c-4", childBus: "i2c-6");

        var g = Assert.Single(LinuxDisplayBrightnessProvider.SelectDdcCandidates(t.At("drm"), t.At("devices")));
        Assert.Empty(g.Primary);
        Assert.Equal(new[] { 4 }, g.Fallback);
    }

    [Fact]
    public void SelectDdcCandidates_SmuDdcLinkIsNeverACandidate()
    {
        using var t = new TempDir();
        t.Write("devices/i2c-4/name", "AMDGPU SMU 0\n");
        t.Write("drm/card1-DP-1/i2c-9/name", "AMDGPU DM aux hw bus 0\n");
        Connector(t, "card1-DP-1", "connected", ddcBus: "i2c-4", childBus: "i2c-9");

        var g = Assert.Single(LinuxDisplayBrightnessProvider.SelectDdcCandidates(t.At("drm"), t.At("devices")));
        Assert.Equal(new[] { 9 }, g.Primary);
        Assert.Empty(g.Fallback);
    }

    [Fact]
    public void SelectDdcCandidates_AuxChildrenArePrimaryAndDdcLinkIsFallback()
    {
        using var t = new TempDir();
        t.Write("devices/i2c-4/name", "AMDGPU DM i2c hw bus 0\n");
        t.Write("drm/card1-DP-1/i2c-9/name", "AMDGPU DM aux hw bus 0\n");
        Connector(t, "card1-DP-1", "connected", ddcBus: "i2c-4", childBus: "i2c-9");

        var g = Assert.Single(LinuxDisplayBrightnessProvider.SelectDdcCandidates(t.At("drm"), t.At("devices")));
        Assert.Equal(new[] { 9 }, g.Primary);
        Assert.Equal(new[] { 4 }, g.Fallback);
    }

    [Fact]
    public void ProbeCandidates_ChildAnswers_LinkIsNeverProbed()
    {
        var probed = new List<int>();
        var groups = new[] { new LinuxDisplayBrightnessProvider.DdcCandidates(new List<int> { 9 }, new List<int> { 4 }) };

        var found = LinuxDisplayBrightnessProvider.ProbeCandidates(groups, b => { probed.Add(b); return b == 9; });

        Assert.Equal(new[] { 9 }, found);
        Assert.Equal(new[] { 9 }, probed);
    }

    [Fact]
    public void ProbeCandidates_NoChildAnswers_LinkIsProbedAndSelected()
    {
        var probed = new List<int>();
        var groups = new[] { new LinuxDisplayBrightnessProvider.DdcCandidates(new List<int> { 9 }, new List<int> { 4 }) };

        var found = LinuxDisplayBrightnessProvider.ProbeCandidates(groups, b => { probed.Add(b); return b == 4; });

        Assert.Equal(new[] { 4 }, found);
        Assert.Equal(new[] { 9, 4 }, probed);
    }

    [Fact]
    public void ProbeCandidates_LogsEachRejectedBusOnce()
    {
        var lines = new List<string>();
        var groups = new[]
        {
            new LinuxDisplayBrightnessProvider.DdcCandidates(new List<int> { 9 }, new List<int>()),
            new LinuxDisplayBrightnessProvider.DdcCandidates(new List<int> { 9 }, new List<int>()),
        };

        LinuxDisplayBrightnessProvider.ProbeCandidates(groups, _ => false, lines.Add);

        Assert.Equal(new[] { "i2c-9 rejected: no VCP 0x10 reply" }, lines);
    }

    [Fact]
    public void ProbeCandidates_ProbesEachBusOnceAcrossGroups()
    {
        var probed = new List<int>();
        var groups = new[]
        {
            new LinuxDisplayBrightnessProvider.DdcCandidates(new List<int> { 9 }, new List<int>()),
            new LinuxDisplayBrightnessProvider.DdcCandidates(new List<int> { 9 }, new List<int>()),
        };

        LinuxDisplayBrightnessProvider.ProbeCandidates(groups, b => { probed.Add(b); return true; });

        Assert.Equal(new[] { 9 }, probed);
    }

    [Fact]
    public void SelectDdcCandidates_TakesEveryI2cChildOfAConnector()
    {
        using var t = new TempDir();
        t.Write("drm/card0-DP-1/i2c-5/name", "AMDGPU DM aux\n");
        t.Write("drm/card0-DP-1/i2c-6/name", "AMDGPU DM aux\n");
        Connector(t, "card0-DP-1", "connected");

        Assert.Equal(new[] { 5, 6 }, Primaries(t));
        Assert.Empty(Fallbacks(t));
    }

    private sealed class ScanFixture
    {
        public int Scans;
        public string Signature = "a";
        public bool HasConnectors = true;
        public long Now;
        public int Brightness = 40;
        public LinuxDisplayBrightnessProvider Provider { get; }

        public ScanFixture()
        {
            Provider = new LinuxDisplayBrightnessProvider(() => (Signature, HasConnectors), Scan, () => Now);
        }

        private IEnumerable<LinuxDisplayBrightnessProvider.Target> Scan()
        {
            Scans++;
            var dto = new DisplayDto
            {
                Id = "linux-ddc-i2c-5",
                IsDdcCapable = true,
                BrightnessControl = new DisplayBrightnessControlDto { Supported = true, Current = Brightness },
            };
            yield return new LinuxDisplayBrightnessProvider.Target(false, null, 5, dto);
        }
    }

    [Fact]
    public void Enumerate_ScansOnceWhileSignatureIsUnchanged()
    {
        var f = new ScanFixture();
        f.Provider.Enumerate();
        f.Provider.Enumerate();
        f.Provider.FindDisplayIdByHardwareName(new[] { "i2c-5" });
        Assert.Equal(1, f.Scans);
    }

    [Fact]
    public void Enumerate_RescansWhenSignatureChanges()
    {
        var f = new ScanFixture();
        f.Provider.Enumerate();
        f.Signature = "b";
        f.Provider.Enumerate();
        f.Provider.Enumerate();
        Assert.Equal(2, f.Scans);
    }

    [Fact]
    public void Enumerate_RetriesAScanThatRejectedABusUntilTheRetryCap()
    {
        var stats = new LinuxDisplayBrightnessProvider.ScanStats();
        var scans = 0;
        long now = 0;
        var p = new LinuxDisplayBrightnessProvider(
            () => ("a", true),
            () =>
            {
                scans++;
                stats.Unanswered = 1;
                return Array.Empty<LinuxDisplayBrightnessProvider.Target>();
            },
            () => now,
            stats);

        p.Enumerate();
        p.Enumerate();
        Assert.Equal(1, scans);

        for (var i = 1; i <= LinuxDisplayBrightnessProvider.MaxRejectRetries + 2; i++)
        {
            now = i * (LinuxDisplayBrightnessProvider.ForcedRescanMinMs << LinuxDisplayBrightnessProvider.MaxRejectRetries);
            p.Enumerate();
        }
        Assert.Equal(1 + LinuxDisplayBrightnessProvider.MaxRejectRetries, scans);
    }

    [Fact]
    public void RequestRescan_DoesNotRearmTheRetryCap()
    {
        var stats = new LinuxDisplayBrightnessProvider.ScanStats();
        var scans = 0;
        long now = 0;
        var p = new LinuxDisplayBrightnessProvider(
            () => ("a", true),
            () =>
            {
                scans++;
                stats.Unanswered = 1;
                return Array.Empty<LinuxDisplayBrightnessProvider.Target>();
            },
            () => now,
            stats);

        for (var i = 0; i <= LinuxDisplayBrightnessProvider.MaxRejectRetries; i++)
        {
            now = i * (LinuxDisplayBrightnessProvider.ForcedRescanMinMs << LinuxDisplayBrightnessProvider.MaxRejectRetries);
            p.Enumerate();
        }
        var before = scans;
        now += LinuxDisplayBrightnessProvider.ForcedRescanMinMs;
        p.RequestRescan();
        now += LinuxDisplayBrightnessProvider.ForcedRescanMinMs;
        p.Enumerate();
        Assert.Equal(before + 1, scans);
    }

    [Fact]
    public void Enumerate_RetryGapDoublesSoThePollingRateCannotSpendTheBudget()
    {
        var stats = new LinuxDisplayBrightnessProvider.ScanStats();
        var scans = 0;
        long now = 0;
        var p = new LinuxDisplayBrightnessProvider(
            () => ("a", true),
            () =>
            {
                scans++;
                stats.Unanswered = 1;
                return Array.Empty<LinuxDisplayBrightnessProvider.Target>();
            },
            () => now,
            stats);

        var gap = LinuxDisplayBrightnessProvider.ForcedRescanMinMs;
        p.Enumerate();
        // A fast poller must still see each gap double after every scan.
        var lastScanAt = 0L;
        var expectedGap = gap;
        for (now = 5_000; now <= 20 * 60_000; now += 5_000)
        {
            var before = scans;
            p.Enumerate();
            if (scans > before)
            {
                Assert.True(now - lastScanAt >= expectedGap);
                lastScanAt = now;
                expectedGap *= 2;
            }
        }
        var window = 20 * 60_000;
        Assert.InRange(scans, 4, 1 + LinuxDisplayBrightnessProvider.MaxRejectRetries);
        Assert.True(lastScanAt >= window * 3 / 4);
    }

    [Fact]
    public void Enumerate_RetryWaitsOutTheRescanGap()
    {
        var stats = new LinuxDisplayBrightnessProvider.ScanStats();
        var scans = 0;
        long now = 0;
        var p = new LinuxDisplayBrightnessProvider(
            () => ("a", true),
            () =>
            {
                scans++;
                stats.Unanswered = 1;
                return Array.Empty<LinuxDisplayBrightnessProvider.Target>();
            },
            () => now,
            stats);

        p.Enumerate();
        now = LinuxDisplayBrightnessProvider.ForcedRescanMinMs - 1;
        p.Enumerate();
        Assert.Equal(1, scans);
    }

    [Fact]
    public void Enumerate_StopsRetryingOnceEveryBusAnswers()
    {
        var stats = new LinuxDisplayBrightnessProvider.ScanStats();
        var scans = 0;
        long now = 0;
        var p = new LinuxDisplayBrightnessProvider(
            () => ("a", true),
            () =>
            {
                scans++;
                stats.Unanswered = scans == 1 ? 1 : 0;
                return Array.Empty<LinuxDisplayBrightnessProvider.Target>();
            },
            () => now,
            stats);

        p.Enumerate();
        now = LinuxDisplayBrightnessProvider.ForcedRescanMinMs;
        p.Enumerate();
        now *= 3;
        p.Enumerate();
        Assert.Equal(2, scans);
    }

    [Fact]
    public void RequestRescan_ScansImmediatelyOrIsDropped()
    {
        var f = new ScanFixture();
        f.Provider.Enumerate();

        f.Now = 1_000;
        f.Provider.RequestRescan();
        Assert.Equal(1, f.Scans);

        f.Now = LinuxDisplayBrightnessProvider.ForcedRescanMinMs;
        f.Provider.Enumerate();
        Assert.Equal(1, f.Scans);

        f.Provider.RequestRescan();
        Assert.Equal(2, f.Scans);

        f.Now += 1_000;
        f.Provider.RequestRescan();
        f.Provider.Enumerate();
        Assert.Equal(2, f.Scans);
    }

    [Fact]
    public void GetBrightness_MissedIdForcesOneRescanUntilSignatureChanges()
    {
        var f = new ScanFixture();
        f.Provider.Enumerate();
        f.Now = LinuxDisplayBrightnessProvider.ForcedRescanMinMs;
        Assert.Null(f.Provider.GetBrightness("missing"));
        Assert.Equal(2, f.Scans);

        f.Now += LinuxDisplayBrightnessProvider.ForcedRescanMinMs;
        for (var i = 0; i < 5; i++)
            f.Provider.GetBrightness("missing");
        Assert.Equal(2, f.Scans);

        f.Signature = "b";
        f.Provider.Enumerate();
        Assert.Equal(3, f.Scans);
        f.Now += LinuxDisplayBrightnessProvider.ForcedRescanMinMs;
        f.Provider.GetBrightness("missing");
        Assert.Equal(4, f.Scans);
    }

    [Fact]
    public void Enumerate_WithoutConnectorsRescansOnSlowTtl()
    {
        var f = new ScanFixture { HasConnectors = false };
        f.Provider.Enumerate();
        f.Now = LinuxDisplayBrightnessProvider.NoConnectorRescanMs - 1;
        f.Provider.Enumerate();
        Assert.Equal(1, f.Scans);
        f.Now = LinuxDisplayBrightnessProvider.NoConnectorRescanMs;
        f.Provider.Enumerate();
        Assert.Equal(2, f.Scans);
    }

    [Fact]
    public void Enumerate_WithConnectorsNeverAgesOut()
    {
        var f = new ScanFixture();
        f.Provider.Enumerate();
        f.Now = LinuxDisplayBrightnessProvider.NoConnectorRescanMs * 10;
        f.Provider.Enumerate();
        Assert.Equal(1, f.Scans);
    }

    [Fact]
    public void RememberBrightness_DoesNotMutateDtoAlreadyReturned()
    {
        var f = new ScanFixture();
        var before = f.Provider.Enumerate().Single(d => d.Id == "linux-ddc-i2c-5");
        f.Provider.RememberBrightness("linux-ddc-i2c-5", 77);
        Assert.Equal(40, before.BrightnessControl.Current);
    }

    [Fact]
    public void Enumerate_ExcludedDisplayDoesNotLeakIntoCache()
    {
        var f = new ScanFixture();
        var excluded = f.Provider.Enumerate(new[] { "linux-ddc-i2c-5" });
        Assert.False(excluded.Single(d => d.Id == "linux-ddc-i2c-5").DdcEnabled);
        Assert.True(f.Provider.Enumerate().Single(d => d.Id == "linux-ddc-i2c-5").DdcEnabled);
        Assert.Equal(1, f.Scans);
    }

    private static string Sig(TempDir t) => LinuxDisplayBrightnessProvider.ComputeDisplaySignature(t.At("drm"), t.At("dev")).Signature;

    [Fact]
    public void ComputeDisplaySignature_ReportsWhetherAnyConnectorHasStatus()
    {
        using var t = new TempDir();
        t.Write("dev/i2c-3", "");
        Assert.False(LinuxDisplayBrightnessProvider.ComputeDisplaySignature(t.At("drm"), t.At("dev")).HasConnectors);
        t.Write("drm/card0-DP-1/status", "disconnected\n");
        Assert.True(LinuxDisplayBrightnessProvider.ComputeDisplaySignature(t.At("drm"), t.At("dev")).HasConnectors);
    }

    [Fact]
    public void ComputeDisplaySignature_ChangesWithStatusEdidAndI2cNodes()
    {
        using var t = new TempDir();
        t.Write("drm/card0-DP-1/status", "connected\n");
        t.Write("drm/card0-DP-1/edid", "one");
        t.Write("dev/i2c-3", "");
        var baseline = Sig(t);
        Assert.Equal(baseline, Sig(t));

        t.Write("drm/card0-DP-1/edid", "two");
        var afterEdid = Sig(t);
        Assert.NotEqual(baseline, afterEdid);

        t.Write("drm/card0-DP-1/status", "disconnected\n");
        var afterStatus = Sig(t);
        Assert.NotEqual(afterEdid, afterStatus);

        t.Write("dev/i2c-4", "");
        Assert.NotEqual(afterStatus, Sig(t));
    }

    [Fact]
    public void RememberBrightness_UpdatesCachedCurrentWithoutRescan()
    {
        var f = new ScanFixture();
        f.Provider.Enumerate();
        f.Provider.RememberBrightness("linux-ddc-i2c-5", 77);
        var dto = f.Provider.Enumerate().Single(d => d.Id == "linux-ddc-i2c-5");
        Assert.Equal(77, dto.BrightnessControl.Current);
        Assert.Equal(1, f.Scans);
    }
}
