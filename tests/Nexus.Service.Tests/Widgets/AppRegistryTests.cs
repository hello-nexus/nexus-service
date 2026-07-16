using System;
using System.Collections.Generic;
using System.IO;
using Nexus.Service.Widgets;

namespace Nexus.Service.Tests.Widgets;

public class AppRegistryTests : IDisposable
{
    private readonly string _root;

    public AppRegistryTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "nexus-widgets-tests-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_root);
        CopyFixture(Path.Combine(AppContext.BaseDirectory, "Widgets", "Fixtures", "widgets-basic"), _root);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
    }

    private AppRegistry NewRegistry(AppInstallPaths.Source source = AppInstallPaths.Source.User)
    {
        return new AppRegistry(() => new List<AppInstallPaths.Root>
        {
            new(_root, source),
        });
    }

    [Fact]
    public void Discovers_fixture_widget()
    {
        var registry = NewRegistry();
        Assert.True(registry.TryGet("com.hellonexus.fixture-basic", out var entry));
        Assert.Equal("1.0.0", entry.Manifest.Version);
        Assert.Equal("nexus.app/1", entry.Manifest.Schema);
        Assert.Single(entry.Manifest.Surfaces, "dashboard");
        Assert.Equal(new List<string> { "cpu.*" }, entry.Manifest.Capabilities.SensorsRead);
        Assert.Equal("2x2", entry.Manifest.DefaultSize);
        Assert.Equal("sdk", entry.Manifest.Runtime);
    }

    [Fact]
    public void Skips_bundle_when_folder_name_does_not_match_id()
    {
        // Rename the folder so manifest.id no longer matches the directory.
        var src = Path.Combine(_root, "com.hellonexus.fixture-basic");
        var dst = Path.Combine(_root, "imposter.bundle");
        Directory.Move(src, dst);

        var registry = NewRegistry();
        Assert.False(registry.TryGet("com.hellonexus.fixture-basic", out _));
        Assert.False(registry.TryGet("imposter.bundle", out _));
    }

    [Fact]
    public void Skips_bundle_when_runtime_is_not_sdk()
    {
        var manifest = Path.Combine(_root, "com.hellonexus.fixture-basic", "manifest.json");
        File.WriteAllText(manifest, """
        {
          "schema": "nexus.app/1",
          "id": "com.hellonexus.fixture-basic",
          "name": "x",
          "version": "1.0.0",
          "min_nexus_version": "0.42.0",
          "surfaces": ["dashboard"],
          "capabilities": {}
        }
        """);

        var registry = NewRegistry();
        Assert.False(registry.TryGet("com.hellonexus.fixture-basic", out _));
    }

    [Fact]
    public void Skips_sdk_bundle_when_widget_mjs_is_missing()
    {
        // Manifest declares the SDK runtime but the bundle ships no widget.mjs.
        File.Delete(Path.Combine(_root, "com.hellonexus.fixture-basic", "widget.mjs"));

        var registry = NewRegistry();
        Assert.False(registry.TryGet("com.hellonexus.fixture-basic", out _));
    }

    [Fact]
    public void Skips_bundle_when_manifest_is_malformed()
    {
        var manifest = Path.Combine(_root, "com.hellonexus.fixture-basic", "manifest.json");
        File.WriteAllText(manifest, "{ not valid json");

        var registry = NewRegistry();
        Assert.False(registry.TryGet("com.hellonexus.fixture-basic", out _));
    }

    [Fact]
    public void Skips_bundle_with_unknown_schema()
    {
        var manifest = Path.Combine(_root, "com.hellonexus.fixture-basic", "manifest.json");
        File.WriteAllText(manifest, """
        {
          "schema": "nexus.widget/9999",
          "id": "com.hellonexus.fixture-basic",
          "name": "x",
          "version": "1.0.0",
          "min_nexus_version": "0.42.0",
          "surfaces": ["dashboard"],
          "capabilities": {}
        }
        """);

        var registry = NewRegistry();
        Assert.False(registry.TryGet("com.hellonexus.fixture-basic", out _));
    }

    [Fact]
    public void Records_install_source()
    {
        var registry = NewRegistry(AppInstallPaths.Source.Bundled);
        Assert.True(registry.TryGet("com.hellonexus.fixture-basic", out var entry));
        Assert.Equal(AppInstallPaths.Source.Bundled, entry.Source);
    }

    [Fact]
    public void First_root_wins_when_id_appears_in_multiple_roots()
    {
        // Build a second root that also contains the same id.
        var secondRoot = Path.Combine(Path.GetTempPath(), "nexus-widgets-tests-2-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(secondRoot);
        CopyFixture(Path.Combine(AppContext.BaseDirectory, "Widgets", "Fixtures", "widgets-basic"), secondRoot);
        try
        {
            var registry = new AppRegistry(() => new List<AppInstallPaths.Root>
            {
                new(_root, AppInstallPaths.Source.User),
                new(secondRoot, AppInstallPaths.Source.Bundled),
            });

            Assert.True(registry.TryGet("com.hellonexus.fixture-basic", out var entry));
            Assert.Equal(AppInstallPaths.Source.User, entry.Source);
        }
        finally
        {
            try { Directory.Delete(secondRoot, recursive: true); } catch { /* best effort */ }
        }
    }

    private static void CopyFixture(string source, string destination)
    {
        foreach (var dir in Directory.EnumerateDirectories(source, "*", SearchOption.AllDirectories))
        {
            Directory.CreateDirectory(dir.Replace(source, destination, StringComparison.Ordinal));
        }
        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            File.Copy(file, file.Replace(source, destination, StringComparison.Ordinal), overwrite: true);
        }
    }
}
