using System;
using System.IO;
using Nexus.Service.Routes;

namespace Nexus.Service.Tests.Widgets;

/// <summary>
/// Direct tests of <see cref="AppRoutes.ResolveBundleFile"/> - the bundle
/// asset path-traversal / symlink guard behind <c>/apps-api/installed/{id}/asset/**</c>
/// and <c>/apps-api/code/**</c>. Tested directly rather than over HTTP
/// because ASP.NET normalizes <c>..</c> out of the request path before routing,
/// so an HTTP test could not reliably deliver a traversal payload to the guard.
/// </summary>
public sealed class AppBundleResolverTests : IDisposable
{
    private readonly string _root;

    public AppBundleResolverTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "nexus-bundle-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(Path.Combine(_root, "sub"));
        File.WriteAllText(Path.Combine(_root, "sub", "app.js"), "console.log(1)");
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
    }

    [Theory]
    [InlineData("../../../etc/passwd")]
    [InlineData("..\\..\\secret")]
    [InlineData("sub/../../escape")]
    [InlineData("../../../etc/x.nxpack")]
    public void Rejects_path_traversal(string requested)
        => Assert.Null(AppRoutes.ResolveBundleFile(_root, requested));

    [Fact]
    public void Rejects_rooted_path()
        => Assert.Null(AppRoutes.ResolveBundleFile(_root, "/etc/passwd"));

    [Fact]
    public void Rejects_missing_file()
        => Assert.Null(AppRoutes.ResolveBundleFile(_root, "sub/nope.js"));

    [Fact]
    public void Resolves_real_file_under_root()
    {
        var resolved = AppRoutes.ResolveBundleFile(_root, "sub/app.js");

        Assert.NotNull(resolved);
        Assert.EndsWith("app.js", resolved);
    }

    [Fact]
    public void Rejects_symlink_escaping_root()
    {
        var outside = Path.Combine(Path.GetTempPath(),
            "nexus-outside-" + Guid.NewGuid().ToString("N")[..8] + ".txt");
        File.WriteAllText(outside, "secret");
        var link = Path.Combine(_root, "link.js");
        try
        {
            File.CreateSymbolicLink(link, outside);
        }
        catch
        {
            File.Delete(outside);
            return; // platform/permission without symlink support - skip
        }

        try
        {
            Assert.Null(AppRoutes.ResolveBundleFile(_root, "link.js"));
        }
        finally
        {
            File.Delete(outside);
        }
    }
}
