using System;
using System.Collections.Generic;
using Nexus.Service.Activity;
using Nexus.Service.Activity.Storage;
using Nexus.Service.Persistence;
using Nexus.Service.Platform.Mac;
using Xunit;

namespace Nexus.Service.Tests.Activity;

/// <summary>ApplyFocus is the seam both the lsappinfo poll and the NSWorkspace observer feed.</summary>
public sealed class MacScreenTimeProviderTests : IDisposable
{
    private sealed class InertConfigStore : IConfigStore
    {
        private readonly NexusSettings _settings = new();
        public string SettingsPath => ":memory:";
        public NexusSettings Load() => _settings;
        public void Update(Action<NexusSettings> mutator) { mutator(_settings); OnChanged?.Invoke(); }
        public void Reload() { }
        public void FlushNow() { }
        public event Action? OnChanged;
    }

    private readonly MacScreenTimeProvider _provider = new(new InMemoryScreenTimeStore(), new InertConfigStore());

    public void Dispose() => _provider.Dispose();

    [Fact]
    public void ApplyFocus_RegularApp_ReportsBundlePathAsExePathAndRaisesFocusChanged()
    {
        var raised = 0;
        _provider.FocusChanged += () => raised++;

        _provider.ApplyFocus("Safari", 77760, "/Applications/Safari.app", "/Applications/Safari.app/Contents/MacOS/Safari", regular: true);

        Assert.Equal(1, raised);
        var details = _provider.GetCurrentFocusDetails();
        Assert.Equal(77760, details?.Pid);
        Assert.Equal("Safari", details?.App);
        Assert.Equal("/Applications/Safari.app", details?.ExePath);
        Assert.Equal("Safari", _provider.GetCurrentSession()?.Name);
    }

    [Fact]
    public void ApplyFocus_NoBundle_FallsBackToTheExecutablePath()
    {
        _provider.ApplyFocus("java", 500, null, "/usr/bin/java", regular: true);

        Assert.Equal("/usr/bin/java", _provider.GetCurrentFocusDetails()?.ExePath);
    }

    [Fact]
    public void ApplyFocus_UiElementAgent_IsNotAFocusChange()
    {
        _provider.ApplyFocus("Safari", 77760, "/Applications/Safari.app", null, regular: true);
        var raised = 0;
        _provider.FocusChanged += () => raised++;

        _provider.ApplyFocus("coreautha", 2406, "/System/Library/Frameworks/LocalAuthentication.framework/Support/coreautha.bundle", null, regular: false);

        Assert.Equal(0, raised);
        Assert.Equal("Safari", _provider.GetCurrentSession()?.Name);
    }

    [Fact]
    public void ApplyFocus_SamePid_IsNotAFocusChange()
    {
        _provider.ApplyFocus("Safari", 77760, "/Applications/Safari.app", null, regular: true);
        var raised = 0;
        _provider.FocusChanged += () => raised++;

        _provider.ApplyFocus("Safari", 77760, "/Applications/Safari.app", null, regular: true);

        Assert.Equal(0, raised);
    }

    [Fact]
    public void ApplyFocus_PidChange_RaisesSessionEndedForThePreviousApp()
    {
        var ended = new List<FocusSessionEnded>();
        _provider.SessionEnded += e => ended.Add(e);
        _provider.ApplyFocus("Safari", 77760, "/Applications/Safari.app", null, regular: true);

        _provider.ApplyFocus("Notes", 1258, "/System/Applications/Notes.app", null, regular: true);

        var last = Assert.Single(ended);
        Assert.Equal(77760, last.Pid);
        Assert.Equal("Safari", last.App);
    }

    [MacOnlyFact]
    public void WorkspaceObserver_StartsWithoutARunLoop()
    {
        // Registration needs AppKit loaded, which Start does itself; delivery needs the run loop MacAppBootstrap pumps.
        Assert.True(MacWorkspaceFocusObserver.Start(_ => { }));
    }
}
