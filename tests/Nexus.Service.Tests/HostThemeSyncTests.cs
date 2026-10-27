using Nexus.Service.Platform;

namespace Nexus.Service.Tests;

public sealed class HostThemeSyncTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "host-theme-" + Guid.NewGuid().ToString("N"));

    public HostThemeSyncTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { }
    }

    private TestableConfigStore NewStore() => new(Path.Combine(_dir, "settings.json"));

    [Theory]
    [InlineData(true, "dark")]
    [InlineData(false, "light")]
    public void Apply_seeds_the_host_theme_whatever_the_app_theme_mode(bool dark, string expected)
    {
        var store = NewStore();
        store.Update(s => s.Theme.ThemeMode = "dark");

        Assert.True(HostThemeSync.Apply(store, dark));

        Assert.Equal(expected, store.Load().Theme.ResolvedThemeMode);
    }

    [Fact]
    public void Apply_reports_no_change_for_the_same_theme()
    {
        var store = NewStore();
        HostThemeSync.Apply(store, true);

        Assert.False(HostThemeSync.Apply(store, true));
    }

    [Fact]
    public void ClearUnsourced_drops_a_stale_value_once()
    {
        var store = NewStore();
        HostThemeSync.Apply(store, true);

        Assert.True(HostThemeSync.ClearUnsourced(store));
        Assert.Equal("", store.Load().Theme.ResolvedThemeMode);
        Assert.False(HostThemeSync.ClearUnsourced(store));
    }
}
