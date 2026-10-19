using System.Collections.Generic;
using System.Linq;
using Nexus.Service.Conflicts;
using Nexus.Service.Sockets;
using Xunit;

namespace Nexus.Service.Tests.Conflicts;

public class ConflictSignerGateTests
{
    private const string MsiSigner = "CN=MICRO-STAR INTERNATIONAL CO., LTD., O=MICRO-STAR INTERNATIONAL CO., LTD., C=TW";
    private const string CorsairSigner = "CN=\"Corsair Memory, Inc.\", O=\"Corsair Memory, Inc.\", C=US";

    private static ConflictAppDefinition App(string id) => ConflictAppCatalog.All.Single(a => a.Id == id);

    private static ConflictSignerGate Gate(
        string? path, ConflictSignerGate.Identity identity, List<string>? lookups = null) =>
        new(_ => path, p =>
        {
            lookups?.Add(p);
            return identity;
        });

    [Fact]
    public void NameAndMatchingSigner_IsFlagged()
    {
        var gate = Gate(@"C:\MSI\ControlCenter.exe", new(MsiSigner, null));
        Assert.True(gate.Matches(App("msi-control-center"), 10));
    }

    [Fact]
    public void ElgatoControlCenterSignedByCorsair_IsNotMsiControlCenter()
    {
        var gate = Gate(@"C:\Elgato\ControlCenter.exe", new(CorsairSigner, null));
        Assert.False(gate.Matches(App("msi-control-center"), 10));
    }

    [Fact]
    public void ASignedExe_IsDecidedBySignerEvenWhenCompanyMatches()
    {
        var gate = Gate(@"C:\x\ControlCenter.exe", new(CorsairSigner, "Micro-Star"));
        Assert.False(gate.Matches(App("msi-control-center"), 10));
    }

    [Fact]
    public void UnsignedExe_FallsBackToCompanyName()
    {
        var gate = Gate(@"C:\x\FanControl.exe", new("", "Rémi Mercier"));
        Assert.True(gate.Matches(App("fan-control"), 10));
        Assert.False(Gate(@"C:\x\FanControl.exe", new("", "Someone Else")).Matches(App("fan-control"), 10));
    }

    [Fact]
    public void UnsignedExeWithNoCompany_IsNotFlagged()
    {
        Assert.False(Gate(@"C:\x\MSIAfterburner.exe", new("", "")).Matches(App("msi-afterburner"), 10));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void UnreadablePath_IsNotFlagged(string? path)
    {
        Assert.False(Gate(path, new(MsiSigner, null)).Matches(App("msi-control-center"), 10));
    }

    [Fact]
    public void ThrowingPathLookup_IsNotFlagged()
    {
        var gate = new ConflictSignerGate(_ => throw new System.UnauthorizedAccessException(), _ => new(MsiSigner, null));
        Assert.False(gate.Matches(App("msi-control-center"), 10));
    }

    [Fact]
    public void AnUngatedEntry_IsNeverLookedUp()
    {
        var lookups = new List<string>();
        var gate = new ConflictSignerGate(_ => { lookups.Add("path"); return null; }, p => { lookups.Add(p); return default; });
        Assert.True(gate.Matches(App("openrgb"), 10));
        Assert.Empty(lookups);
    }

    [Fact]
    public void ElgatoStreamDeck_AcceptsCorsairOrElgatoSigner_NotMsi()
    {
        var def = App(Nexus.Service.Devices.Handlers.StreamDeckHandler.ElgatoConflictAppId);
        Assert.True(Gate(@"C:\x.exe", new(CorsairSigner, null)).Matches(def, 1));
        Assert.True(Gate(@"C:\x.exe", new("CN=Elgato Systems GmbH", null)).Matches(def, 1));
        Assert.False(Gate(@"C:\x.exe", new(MsiSigner, null)).Matches(def, 1));
    }

    [Fact]
    public void ResolvedIdentity_IsCachedPerPidAndPath()
    {
        var lookups = new List<string>();
        var gate = Gate(@"C:\MSI\ControlCenter.exe", new(MsiSigner, null), lookups);
        var def = App("msi-control-center");
        Assert.True(gate.Matches(def, 10));
        Assert.True(gate.Matches(def, 10));
        Assert.Single(lookups);
        Assert.True(gate.Matches(def, 11));
        Assert.Equal(2, lookups.Count);
    }

    [Fact]
    public void AnEmptyIdentity_IsRetriedNextScan()
    {
        var lookups = new List<string>();
        var gate = Gate(@"C:\x.exe", default, lookups);
        var def = App("msi-control-center");
        Assert.False(gate.Matches(def, 10));
        Assert.False(gate.Matches(def, 10));
        Assert.Equal(2, lookups.Count);
    }

    [Fact]
    public void Catalog_PinsExactlyTheAgreedEntries()
    {
        var pinned = ConflictAppCatalog.All.Where(a => a.Signers.Length > 0).Select(a => a.Id).OrderBy(i => i).ToArray();
        var expected = new[]
        {
            "msi-control-center", "msi-afterburner", "icue", Nexus.Service.Devices.Handlers.StreamDeckHandler.ElgatoConflictAppId,
            "nzxt-cam", "razer-synapse", "fan-control",
        }.OrderBy(i => i).ToArray();
        Assert.Equal(expected, pinned);
    }

    [Fact]
    public void Watcher_DoesNotFlagElgatoControlCenterAsMsi_ButFlagsTheRealOne()
    {
        var index = new Dictionary<string, List<(int Pid, string? Path)>>(System.StringComparer.OrdinalIgnoreCase)
        {
            ["ControlCenter"] = new() { (200, @"C:\Elgato\ControlCenter.exe") },
        };
        var elgato = new ConflictSignerGate(_ => null, _ => new(CorsairSigner, null));
        var watcher = new ConflictWatcher(new MultiplexHub(), null, null, null, () => index, null, elgato);
        Assert.Empty(watcher.GetConflicts());

        var msi = new ConflictSignerGate(_ => null, _ => new(MsiSigner, null));
        watcher = new ConflictWatcher(new MultiplexHub(), null, null, null, () => index, null, msi);
        Assert.Equal(new[] { "msi-control-center" }, watcher.GetConflicts().Select(c => c.Id));
    }
}
