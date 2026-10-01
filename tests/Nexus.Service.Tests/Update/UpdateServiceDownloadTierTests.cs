using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Nexus.Service.Devices.Firmware;
using Nexus.Service.Persistence;
using Nexus.Service.Plugins;
using Nexus.Service.Sockets;
using Nexus.Service.Update;
using Xunit;

namespace Nexus.Service.Tests.Update;

/// <summary>
/// Drives UpdateService.CheckNowAsync end to end on this (non-Windows) test
/// host to verify the download-tier status wiring: a newer release is offered
/// with its DownloadUrl even though this host cannot auto-install it, and
/// "always" mode never starts a background download here.
/// </summary>
public sealed class UpdateServiceDownloadTierTests
{
    private const string NewerVersion = "v99.0.0";
    private const string AssetUrl = "https://example.com/releases/Nexus-Linux-x64-99.0.0.tar.gz";

    private static UpdateService BuildService(IUpdateSource source, IConfigStore store) =>
        new(
            source,
            new UpdateDownloader(new ThrowingHttpClientFactory()),
            store,
            TestFirmwareFlasher.Create(),
            new MultiplexHub());

    [Fact]
    public async Task Status_ReportsDownloadTier_WhenNewerReleaseExists()
    {
        var store = new InMemoryConfigStore();
        store.Update(s => s.Update.UpdateMode = "notify");
        var source = new FakeUpdateSource(new UpdateManifest
        {
            Version = NewerVersion,
            Notes = "test release",
            AssetUrl = AssetUrl,
        });
        var svc = BuildService(source, store);

        var status = await svc.CheckNowAsync(CancellationToken.None);

        if (!Nexus.Service.Common.ClientCredential.IsOfficial)
        {
            // OTA is one of ours: an unofficial build returns the untouched
            // snapshot rather than polling, so there is no tier to report.
            Assert.False(status.UpdateAvailable);
            return;
        }

        Assert.True(status.UpdateAvailable);
        Assert.Equal(NewerVersion, status.LatestVersion);
        Assert.Equal(AssetUrl, status.DownloadUrl);
        // This suite never runs on a Windows host (CI's service job is
        // ubuntu-latest; dev runs are macOS), so this exercises the real
        // off-Windows branch of OperatingSystem.IsWindows() at the call site.
        Assert.False(status.CanAutoInstall);
    }

    [Fact]
    public async Task AutoStage_NeverTriggers_OffWindows_EvenInAlwaysMode()
    {
        var store = new InMemoryConfigStore();
        store.Update(s => s.Update.UpdateMode = "always");
        var source = new FakeUpdateSource(new UpdateManifest
        {
            Version = NewerVersion,
            Notes = "test release",
            AssetUrl = AssetUrl,
            Sha256 = new string('a', 64),
            Sha256IsFromSumsFile = true,
        });
        var svc = BuildService(source, store);

        var status = await svc.CheckNowAsync(CancellationToken.None);

        // A background stage would call ThrowingHttpClientFactory.CreateClient
        // and fail into "downloading"/"failed"; "idle" proves it never started.
        Assert.Equal("idle", status.State);
        Assert.False(status.UpdateReady);
        // An unofficial build never polls, so it has no release to offer; the
        // no-background-stage guarantee above holds in both flavours.
        if (Nexus.Service.Common.ClientCredential.IsOfficial)
        {
            Assert.True(status.UpdateAvailable);
        }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Status_CanStage_FollowsWhetherSha256SumsListsTheAsset(bool fromSums)
    {
        var store = new InMemoryConfigStore();
        store.Update(s => s.Update.UpdateMode = "notify");
        var source = new FakeUpdateSource(new UpdateManifest
        {
            Version = NewerVersion,
            Notes = "test release",
            AssetUrl = AssetUrl,
            Sha256 = new string('a', 64),
            Sha256IsFromSumsFile = fromSums,
        });
        var svc = BuildService(source, store);

        var status = await svc.CheckNowAsync(CancellationToken.None);

        // An unofficial build never polls, so no release reaches the status.
        if (!Nexus.Service.Common.ClientCredential.IsOfficial)
        {
            Assert.False(status.CanStage);
            return;
        }
        Assert.Equal(fromSums, status.CanStage);
    }

    private sealed class FakeUpdateSource : IUpdateSource
    {
        private readonly UpdateManifest? _manifest;
        public FakeUpdateSource(UpdateManifest? manifest) { _manifest = manifest; }
        public Task<UpdateManifest?> GetLatestAsync(string channel, CancellationToken ct) =>
            Task.FromResult(_manifest);
    }

    private sealed class ThrowingHttpClientFactory : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) =>
            throw new InvalidOperationException("auto-stage must not run off Windows.");
    }

    private sealed class InMemoryConfigStore : IConfigStore
    {
        private readonly NexusSettings _settings = new();
        public string SettingsPath => ":memory:";
        public NexusSettings Load() => _settings;
        public void Update(Action<NexusSettings> mutator) { mutator(_settings); OnChanged?.Invoke(); }
        public void Reload() { }
        public void FlushNow() { }
        public event Action? OnChanged;
    }
}
