using System;
using System.IO;
using Nexus.Service.Peripherals.StreamDeck.ElgatoImport;
using Xunit;

namespace Nexus.Service.Tests.StreamDeck;

/// <summary>
/// ElgatoProfileLocator.Evaluate selection across candidate dirs - the shape the
/// Windows LocalSystem multi-user-profile scan feeds it (its own %APPDATA% is the
/// SYSTEM profile's, so the real user's store arrives as one of several scanned
/// candidates).
/// </summary>
public sealed class ElgatoProfileLocatorTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "nexus-elgato-locator-" + Guid.NewGuid().ToString("N")[..8]);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
    }

    private string MakeStore(string name, bool v3, bool v2 = false)
    {
        var dir = Path.Combine(_root, name);
        if (v3) { Directory.CreateDirectory(Path.Combine(dir, "ProfilesV3")); }
        if (v2) { Directory.CreateDirectory(Path.Combine(dir, "ProfilesV2")); }
        if (!v3 && !v2) { Directory.CreateDirectory(dir); }
        return dir;
    }

    [Fact]
    public void Evaluate_PicksV3AmongMixedCandidates_IncludingAnEmptySystemProfile()
    {
        var systemProfile = MakeStore("system", v3: false, v2: false); // LocalSystem's own %APPDATA%: no store
        var user = MakeStore("nicol", v3: true);
        var (status, root) = ElgatoProfileLocator.Evaluate(new[] { systemProfile, user });
        Assert.Equal(ElgatoStoreStatus.Ok, status);
        Assert.Equal(Path.Combine(user, "ProfilesV3"), root);
    }

    [Fact]
    public void Evaluate_PrefersNewestV3WhenSeveralUsersHaveOne()
    {
        var older = MakeStore("older", v3: true);
        var newer = MakeStore("newer", v3: true);
        Directory.SetLastWriteTimeUtc(Path.Combine(older, "ProfilesV3"), new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        Directory.SetLastWriteTimeUtc(Path.Combine(newer, "ProfilesV3"), new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        var (status, root) = ElgatoProfileLocator.Evaluate(new[] { older, newer });
        Assert.Equal(ElgatoStoreStatus.Ok, status);
        Assert.Equal(Path.Combine(newer, "ProfilesV3"), root);
    }

    [Fact]
    public void Evaluate_V2OnlyAcrossAllCandidates_IsUnsupportedVersion()
    {
        var a = MakeStore("a", v3: false, v2: true);
        var b = MakeStore("b", v3: false, v2: false);
        var (status, root) = ElgatoProfileLocator.Evaluate(new[] { a, b });
        Assert.Equal(ElgatoStoreStatus.UnsupportedVersion, status);
        Assert.Null(root);
    }

    [Fact]
    public void Evaluate_V3AndV2Present_PrefersV3()
    {
        var v2only = MakeStore("legacy", v3: false, v2: true);
        var v3 = MakeStore("current", v3: true);
        var (status, root) = ElgatoProfileLocator.Evaluate(new[] { v2only, v3 });
        Assert.Equal(ElgatoStoreStatus.Ok, status);
        Assert.Equal(Path.Combine(v3, "ProfilesV3"), root);
    }

    [Fact]
    public void Evaluate_NoStoreAnywhere_IsNotFound()
    {
        var (status, root) = ElgatoProfileLocator.Evaluate(new[]
        {
            Path.Combine(_root, "missing-one"),
            Path.Combine(_root, "missing-two"),
            "",
        });
        Assert.Equal(ElgatoStoreStatus.NotFound, status);
        Assert.Null(root);
    }
}
