using System;
using System.IO;
using System.Linq;
using Nexus.Service.Lighting.Engine.Gpu;
using Xunit;

namespace Nexus.Service.Tests.Lighting.Gpu;

public sealed class DiskBlobCacheTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "nexus-blobcache-" + Guid.NewGuid().ToString("N"));
    private static string N(string seed) =>
        Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(seed))).ToLowerInvariant()[..40];

    public void Dispose()
    {
        try
        { Directory.Delete(_dir, true); }
        catch { }
    }

    private DiskBlobCache Make(long cap = 1024, long maxQueued = DiskBlobCache.DefaultMaxQueuedBytes)
    {
        var c = new DiskBlobCache(_dir, cap, () => true, maxQueued);
        Assert.True(c.WaitReady(TimeSpan.FromSeconds(10)));
        return c;
    }

    [Fact]
    public void Round_trips_and_misses_cleanly()
    {
        var c = Make();

        Assert.Null(c.TryGet(N("aa")));
        c.Put(N("aa"), new byte[] { 1, 2, 3 });
        c.Flush();

        Assert.Equal(new byte[] { 1, 2, 3 }, c.TryGet(N("aa")));
        Assert.Equal(3, new FileInfo(Path.Combine(_dir, N("aa"))).Length);
    }

    [Fact]
    public void Oversize_and_empty_values_are_not_stored()
    {
        var c = Make(cap: 8);

        c.Put(N("big"), new byte[9]);
        c.Put(N("empty"), ReadOnlySpan<byte>.Empty);
        c.Flush();

        Assert.Null(c.TryGet(N("big")));
        Assert.Null(c.TryGet(N("empty")));
    }

    [Fact]
    public void Names_outside_the_hex_form_never_reach_the_file_system()
    {
        var c = Make();

        c.Put("../escape", new byte[] { 1 });
        c.Put("short", new byte[] { 1 });
        c.Put(new string('A', 40), new byte[] { 1 });
        c.Flush();

        Assert.Null(c.TryGet("../escape"));
        Assert.Null(c.TryGet(new string('A', 40)));
        Assert.Empty(new DirectoryInfo(_dir).EnumerateFiles());
    }

    [Fact]
    public void Only_hex_named_files_are_indexed_at_init()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllBytes(Path.Combine(_dir, N("ab")), new byte[] { 1 });
        File.WriteAllBytes(Path.Combine(_dir, "notes.txt"), new byte[] { 2 });
        File.WriteAllBytes(Path.Combine(_dir, new string('Z', 40)), new byte[] { 3 });

        var c = Make();

        Assert.Equal(new byte[] { 1 }, c.TryGet(N("ab")));
        Assert.Null(c.TryGet("notes.txt"));
        Assert.Null(c.TryGet(new string('Z', 40)));
    }

    [Fact]
    public void An_untrusted_directory_means_no_cache()
    {
        var c = new DiskBlobCache(_dir, 1024, () => false);

        Assert.False(c.WaitReady(TimeSpan.FromSeconds(10)));
        c.Put(N("aa"), new byte[] { 1 });
        c.Flush();
        Assert.Null(c.TryGet(N("aa")));
        Assert.False(Directory.Exists(_dir));
    }

    [Theory]
    [InlineData("0123456789abcdef", true)]
    [InlineData("0123456789abcde", false)]
    [InlineData("0123456789ABCDEF", false)]
    [InlineData("0123456789abcdeg", false)]
    [InlineData("", false)]
    public void Cache_name_form(string name, bool expected)
    {
        Assert.Equal(expected, DiskBlobCache.IsCacheName(name));
    }

    [Fact]
    public void Exceeding_the_cap_evicts_least_recently_used_first()
    {
        var c = Make(cap: 35);
        c.Put(N("old"), new byte[10]);
        c.Put(N("mid"), new byte[10]);
        c.Put(N("new"), new byte[10]);
        c.Flush();
        Assert.NotNull(c.TryGet(N("old")));

        c.Put(N("extra"), new byte[10]);
        c.Flush();

        Assert.Null(c.TryGet(N("mid")));
        Assert.NotNull(c.TryGet(N("old")));
        Assert.NotNull(c.TryGet(N("new")));
        Assert.NotNull(c.TryGet(N("extra")));
    }

    [Fact]
    public void Overwriting_a_name_does_not_double_count_its_size()
    {
        var c = Make(cap: 25);

        for (var i = 0; i < 5; i++)
        {
            c.Put(N("same"), new byte[20]);
            c.Flush();
        }

        Assert.NotNull(c.TryGet(N("same")));
    }

    [Fact]
    public void A_put_that_replaces_an_entry_mid_write_is_written_again()
    {
        var c = Make();
        var entered = new System.Threading.ManualResetEventSlim();
        var release = new System.Threading.ManualResetEventSlim();
        var first = true;
        c.BeforeWrite = _ =>
        {
            if (first)
            {
                first = false;
                entered.Set();
                release.Wait(TimeSpan.FromSeconds(10));
            }
        };

        c.Put(N("k"), new byte[] { 1 });
        Assert.True(entered.Wait(TimeSpan.FromSeconds(10)));
        c.Put(N("k"), new byte[] { 2 });
        release.Set();
        c.Flush();

        Assert.Equal(new byte[] { 2 }, Make().TryGet(N("k")));
    }

    [Fact]
    public void Pending_writes_are_capped_by_bytes()
    {
        var c = Make(cap: 1_000_000, maxQueued: 100);
        var release = new System.Threading.ManualResetEventSlim();
        c.BeforeWrite = _ => release.Wait(TimeSpan.FromSeconds(10));

        c.Put(N("a"), new byte[60]);
        c.Put(N("b"), new byte[60]);
        c.Put(N("c"), new byte[30]);
        release.Set();
        c.Flush();

        Assert.NotNull(c.TryGet(N("a")));
        Assert.Null(c.TryGet(N("b")));
        Assert.NotNull(c.TryGet(N("c")));
    }

    [Fact]
    public void A_queued_write_is_readable_before_it_reaches_disk_and_survives_a_new_instance()
    {
        var c = Make();
        c.Put(N("k"), new byte[] { 9 });
        Assert.Equal(new byte[] { 9 }, c.TryGet(N("k")));
        c.Flush();

        Assert.Equal(new byte[] { 9 }, Make().TryGet(N("k")));
    }

    [Fact]
    public void Leftover_temp_files_are_removed_and_never_served()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllBytes(Path.Combine(_dir, "x.abc.tmp"), new byte[50]);

        var c = Make();

        Assert.Null(c.TryGet("x.abc.tmp"));
        Assert.False(File.Exists(Path.Combine(_dir, "x.abc.tmp")));
    }

    [Fact]
    public void Hex_name_is_lowercase_and_stable()
    {
        Assert.Equal("00ff10", DiskBlobCache.HexName(new byte[] { 0x00, 0xFF, 0x10 }));
    }

    [Theory]
    [InlineData("fire", 0, false, "t", "1", "fire", 0, false, "t", "1", true)]
    [InlineData("fire", 0, false, "t", "1", "fire", 1, false, "t", "1", false)]
    [InlineData("fire", 0, false, "t", "1", "fire", 0, true, "t", "1", false)]
    [InlineData("fire", 0, false, "t", "1", "plasma", 0, false, "t", "1", false)]
    [InlineData("fire", 0, false, "t", "1", "fire", 0, false, "u", "1", false)]
    [InlineData("fire", 0, false, "t", "1", "fire", 0, false, "t", "2", false)]
    public void Thumbnail_name_changes_with_every_input(
        string e1, int s1, bool f1, string t1, string v1,
        string e2, int s2, bool f2, string t2, string v2, bool same)
    {
        var a = DiskBlobCache.ThumbnailName(e1, s1, f1, t1, v1);
        var b = DiskBlobCache.ThumbnailName(e2, s2, f2, t2, v2);

        Assert.Equal(same, a == b);
    }
}

public class BlobCachePolicyTests
{
    private const string System_ = "S-1-5-18";
    private const string Admins = "S-1-5-32-544";
    private const string Users = "S-1-5-32-545";
    private const string Everyone = "S-1-1-0";
    private const string AuthenticatedUsers = "S-1-5-11";
    private const string Interactive = "S-1-5-4";
    private const string Installer = "S-1-5-80-956008885-3418522649-1831038044-1853292631-2271478464";

    private const int ReadExecute = 0x1 | 0x8 | 0x20 | 0x80 | 0x20000;
    private const int FullControl = 0x1F01FF;

    [Fact]
    public void A_stock_program_files_list_is_accepted()
    {
        var aces = new[]
        {
            new AceInfo(System_, true, FullControl),
            new AceInfo(Admins, true, FullControl),
            new AceInfo(Installer, true, FullControl),
            new AceInfo(Users, true, ReadExecute),
            new AceInfo("S-1-3-0", true, FullControl),
        };

        Assert.False(BlobCachePolicy.GrantsNonAdminWrite(aces, out _));
    }

    [Theory]
    [InlineData(Users, 0x2)]
    [InlineData(Users, 0x4)]
    [InlineData(Users, 0x10000)]
    [InlineData(Users, 0x40)]
    [InlineData(Users, 0x40000)]
    [InlineData(Users, 0x80000)]
    [InlineData(Users, 0x100)]
    [InlineData(Users, 0x10)]
    [InlineData(Users, FullControl)]
    [InlineData(Users, 0x40000000)]
    [InlineData(Everyone, 0x2)]
    [InlineData(AuthenticatedUsers, 0x4)]
    [InlineData(Interactive, 0x10000)]
    [InlineData("S-1-5-21-1-2-3-1001", FullControl)]
    public void Write_like_rights_for_a_non_admin_are_refused(string sid, int rights)
    {
        var aces = new[] { new AceInfo(System_, true, FullControl), new AceInfo(sid, true, rights) };

        Assert.True(BlobCachePolicy.GrantsNonAdminWrite(aces, out var found));
        Assert.Equal(sid, found);
    }

    [Fact]
    public void Deny_entries_and_read_only_grants_do_not_count()
    {
        var aces = new[]
        {
            new AceInfo(Users, false, FullControl),
            new AceInfo(Everyone, true, ReadExecute),
        };

        Assert.False(BlobCachePolicy.GrantsNonAdminWrite(aces, out _));
    }

    [Theory]
    [InlineData("S-1-5-18", true)]
    [InlineData("S-1-5-32-544", true)]
    [InlineData("S-1-5-80-956008885-3418522649-1831038044-1853292631-2271478464", true)]
    [InlineData("S-1-5-32-545", false)]
    [InlineData("S-1-5-21-1-2-3-1001", false)]
    [InlineData("S-1-3-0", false)]
    [InlineData(null, false)]
    public void Only_admin_owners_are_accepted(string? sid, bool expected)
    {
        Assert.Equal(expected, BlobCachePolicy.IsAdminOwner(sid));
    }

    [Fact]
    public void An_empty_list_grants_nothing()
    {
        Assert.False(BlobCachePolicy.GrantsNonAdminWrite(System.Array.Empty<AceInfo>(), out _));
    }
}
