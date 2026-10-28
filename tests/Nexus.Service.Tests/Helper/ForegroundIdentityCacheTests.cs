using System;
using Nexus.Service.Helper;
using Xunit;

namespace Nexus.Service.Tests.Helper;

public class ForegroundIdentityCacheTests
{
    [Theory]
    [InlineData(@"C:\Windows\System32\notepad.exe", "notepad")]
    [InlineData(@"C:\Windows\explorer.EXE", "explorer")]
    [InlineData(@"C:\Program Files\App\my.tool.exe", "my.tool")]
    [InlineData(@"C:\apps\legacy.bin", "legacy.bin")]
    [InlineData(@"C:\apps\noext", "noext")]
    [InlineData("bare.exe", "bare")]
    [InlineData(@"C:\apps\", "")]
    public void ProcessNameFromPath_MatchesProcessNameForm(string path, string expected)
    {
        Assert.Equal(expected, ForegroundIdentityCache.ProcessNameFromPath(path));
    }

    [Fact]
    public void TryGet_MissesWhenEmpty()
    {
        var cache = new ForegroundIdentityCache();
        Assert.False(cache.TryGet(new IntPtr(10), 5, out _, out _));
    }

    [Fact]
    public void TryGet_HitsOnSameHwndAndPid()
    {
        var cache = new ForegroundIdentityCache();
        cache.Store(new IntPtr(10), 5, "notepad", @"C:\Windows\notepad.exe");

        Assert.True(cache.TryGet(new IntPtr(10), 5, out var app, out var path));
        Assert.Equal("notepad", app);
        Assert.Equal(@"C:\Windows\notepad.exe", path);
    }

    [Theory]
    [InlineData(11, 5)]
    [InlineData(10, 6)]
    public void TryGet_MissesWhenHwndOrPidChanged(int hwnd, int pid)
    {
        var cache = new ForegroundIdentityCache();
        cache.Store(new IntPtr(10), 5, "notepad", null);

        Assert.False(cache.TryGet(new IntPtr(hwnd), pid, out _, out _));
    }

    [Fact]
    public void TryGet_HitsWithNullPath()
    {
        var cache = new ForegroundIdentityCache();
        cache.Store(new IntPtr(10), 5, "taskmgr", null);

        Assert.True(cache.TryGet(new IntPtr(10), 5, out var app, out var path));
        Assert.Equal("taskmgr", app);
        Assert.Null(path);
    }

    [Fact]
    public void Clear_Invalidates()
    {
        var cache = new ForegroundIdentityCache();
        cache.Store(new IntPtr(10), 5, "notepad", null);
        cache.Clear();

        Assert.False(cache.TryGet(new IntPtr(10), 5, out _, out _));
    }

    [Fact]
    public void Store_ReplacesPreviousEntry()
    {
        var cache = new ForegroundIdentityCache();
        cache.Store(new IntPtr(10), 5, "a", null);
        cache.Store(new IntPtr(20), 6, "b", null);

        Assert.False(cache.TryGet(new IntPtr(10), 5, out _, out _));
        Assert.True(cache.TryGet(new IntPtr(20), 6, out var app, out _));
        Assert.Equal("b", app);
    }
}
