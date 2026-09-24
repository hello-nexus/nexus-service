using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Nexus.Service.Models.Widgets;
using Nexus.Service.Store;
using Nexus.Service.Widgets;
using Xunit;

namespace Nexus.Service.Tests.Store;

public class StoreAppUpdaterTests
{
    private readonly List<AppEntry> _installed = new();
    private readonly Dictionary<string, StoreCatalogVersion?> _catalog = new();
    private readonly List<StoreInstallRequest> _installs = new();
    private bool _entitled = true;
    private bool _installOk = true;
    private int _announced;

    private StoreAppUpdater Updater() => new(
        () => _installed,
        (id, _) => Task.FromResult(_catalog.GetValueOrDefault(id)),
        (id, version, _) => Task.FromResult(_entitled
            ? new StoreInstallRequest { AppId = id, Version = version.Version, Sha256 = version.Sha256, Size = version.Size }
            : null),
        (req, _) =>
        {
            _installs.Add(req);
            return Task.FromResult(new StoreInstallResponse { AppId = req.AppId ?? "", Version = req.Version ?? "", Ok = _installOk, Reason = _installOk ? null : "hash_mismatch" });
        },
        () => _announced++);

    private void Installed(string id, string version, AppInstallPaths.Source source = AppInstallPaths.Source.User) =>
        _installed.Add(new AppEntry { Id = id, RootPath = "/apps/" + id, Source = source, Manifest = new AppManifest { Id = id, Version = version } });

    private void Catalog(string id, string version) =>
        _catalog[id] = new StoreCatalogVersion { Version = version, Sha256 = new string('a', 64), Size = 10 };

    [Fact]
    public async Task InstallsTheCatalogVersionWhenItIsNewerAndAnnouncesOnce()
    {
        Installed("com.x.one", "1.0.0");
        Installed("com.x.two", "2.0.0-beta.1");
        Catalog("com.x.one", "1.1.0");
        Catalog("com.x.two", "2.0.0");

        var updated = await Updater().TickAsync(CancellationToken.None);

        Assert.Equal(new[] { "com.x.one", "com.x.two" }, updated);
        Assert.Equal(new[] { "1.1.0", "2.0.0" }, _installs.Select(r => r.Version));
        Assert.Equal(1, _announced);
    }

    [Fact]
    public async Task LeavesAnAppAtOrAboveTheCatalogVersion()
    {
        Installed("com.x.same", "1.1.0");
        Installed("com.x.ahead", "3.0.0");
        Catalog("com.x.same", "1.1.0");
        Catalog("com.x.ahead", "2.9.9");

        Assert.Empty(await Updater().TickAsync(CancellationToken.None));
        Assert.Empty(_installs);
        Assert.Equal(0, _announced);
    }

    [Fact]
    public async Task SkipsBundledCopiesAndAppsTheCatalogDoesNotKnow()
    {
        Installed("com.x.bundled", "1.0.0", AppInstallPaths.Source.Bundled);
        Installed("com.x.sideloaded", "1.0.0");
        Catalog("com.x.bundled", "9.0.0");

        Assert.Empty(await Updater().TickAsync(CancellationToken.None));
        Assert.Empty(_installs);
    }

    [Fact]
    public async Task DoesNotInstallWithoutAnEntitlement()
    {
        Installed("com.x.paid", "1.0.0");
        Catalog("com.x.paid", "1.1.0");
        _entitled = false;

        Assert.Empty(await Updater().TickAsync(CancellationToken.None));
        Assert.Empty(_installs);
        Assert.Equal(0, _announced);
    }

    [Fact]
    public async Task AFailedInstallIsNotAnnounced()
    {
        Installed("com.x.one", "1.0.0");
        Catalog("com.x.one", "1.1.0");
        _installOk = false;

        Assert.Empty(await Updater().TickAsync(CancellationToken.None));
        Assert.Single(_installs);
        Assert.Equal(0, _announced);
    }
}
