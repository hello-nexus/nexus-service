using System;
using System.IO;
using Nexus.Service.Activity;
using Xunit;

namespace Nexus.Service.Tests.Activity;

/// <summary>
/// A fully-qualified .app path is a shortcut id in its own right, so an app
/// outside the enumerated Applications folders (Recent Apps reports the focused
/// app's bundle path) still launches and resolves its display name.
/// </summary>
public sealed class MacShortcutsProviderTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "nexus-mac-shortcuts-" + Guid.NewGuid().ToString("N"));
    private readonly MacAppIconExtractor _extractor = new();

    public void Dispose()
    {
        _extractor.Dispose();
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    private string WriteBundle(string fileName, string displayName)
    {
        var bundle = Path.Combine(_root, fileName);
        var contents = Path.Combine(bundle, "Contents");
        Directory.CreateDirectory(contents);
        File.WriteAllText(Path.Combine(contents, "Info.plist"), $"""
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0">
<dict>
  <key>CFBundleIdentifier</key><string>com.example.test</string>
  <key>CFBundleDisplayName</key><string>{displayName}</string>
</dict>
</plist>
""");
        return bundle;
    }

    [MacOnlyFact]
    public void GetById_BundlePath_SynthesizesShortcutWithPlistDisplayName()
    {
        var bundle = WriteBundle("Visual Studio Code.app", "Code");
        var provider = new MacShortcutsProvider(_extractor);

        var shortcut = provider.GetById(bundle);

        Assert.NotNull(shortcut);
        Assert.Equal(bundle, shortcut!.Id);
        Assert.Equal(bundle, shortcut.Path);
        Assert.Equal("Visual Studio Code", shortcut.Name);
        Assert.Equal("Code", shortcut.ProcessName);
        Assert.Equal("Code", provider.ResolveProcessName(bundle));
    }

    [MacOnlyFact]
    public void GetById_MissingOrRelativeBundlePath_IsNotAShortcut()
    {
        var provider = new MacShortcutsProvider(_extractor);

        Assert.Null(provider.GetById(Path.Combine(_root, "Missing.app")));
        Assert.Null(provider.GetById("Relative.app"));
        Assert.Null(provider.GetById("/definitely/not/a/shortcut"));
    }
}
