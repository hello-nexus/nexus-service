// The hardware auto-installer must fetch the store copy even when the build
// already bundles the same id, so it keys on the install ROOT, not on the id
// being present. com.ibuypower.control shipped bundled before it moved to the
// store, so on the exact hardware this targets both copies can exist.

using System;
using System.Collections.Generic;
using System.IO;
using Nexus.Service.Widgets;
using Xunit;

namespace Nexus.Service.Tests;

public class HardwareAppSourceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "nexus-hw-src-" + Guid.NewGuid().ToString("N"));

    private string MakeApp(string source, string id, string version)
    {
        var dir = Path.Combine(_root, source, id);
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "manifest.json"), $$"""
        {
          "schema": "nexus.app/1",
          "id": "{{id}}",
          "name": "iBUYPOWER",
          "version": "{{version}}",
          "runtime": "sdk",
          "sizes": ["2x2"]
        }
        """);
        File.WriteAllText(Path.Combine(dir, "widget.mjs"), "export default {};");
        return dir;
    }

    private AppRegistry Registry(bool user, bool bundled)
    {
        var roots = new List<AppInstallPaths.Root>();
        if (user)
        {
            MakeApp("user", "com.ibuypower.control", "2.0.0");
            roots.Add(new AppInstallPaths.Root(Path.Combine(_root, "user"), AppInstallPaths.Source.User));
        }
        if (bundled)
        {
            MakeApp("bundled", "com.ibuypower.control", "1.0.0");
            roots.Add(new AppInstallPaths.Root(Path.Combine(_root, "bundled"), AppInstallPaths.Source.Bundled));
        }
        return new AppRegistry(() => roots);
    }

    [Fact]
    public void ABundledCopyReportsTheBundledSource()
    {
        Assert.True(Registry(user: false, bundled: true).TryGet("com.ibuypower.control", out var entry));
        Assert.Equal(AppInstallPaths.Source.Bundled, entry.Source);
    }

    [Fact]
    public void AUserCopyShadowsTheBundledOneAndListsOnce()
    {
        var registry = Registry(user: true, bundled: true);

        Assert.True(registry.TryGet("com.ibuypower.control", out var entry));
        Assert.Equal(AppInstallPaths.Source.User, entry.Source);
        Assert.Equal("2.0.0", entry.Manifest.Version);
        Assert.Single(registry.All());
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); }
        catch (IOException) { }
    }
}
