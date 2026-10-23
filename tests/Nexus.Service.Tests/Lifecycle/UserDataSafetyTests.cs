using Nexus.Service.Lifecycle;

namespace Nexus.Service.Tests.Lifecycle;

public class UserDataSafetyTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "nexus-test-uds-" + Guid.NewGuid().ToString("N")[..8]);

    public UserDataSafetyTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    private static bool CanLink => !OperatingSystem.IsWindows();

    [Fact]
    public void Plain_tree_is_deleted()
    {
        var dir = Path.Combine(_root, "AppData", "Local", "Nexus");
        Directory.CreateDirectory(Path.Combine(dir, "DesktopWebView2"));
        File.WriteAllText(Path.Combine(dir, "DesktopWebView2", "f"), "x");
        var log = new List<string>();

        Assert.True(UserDataSafety.TryDeleteUserDir(dir, log.Add));

        Assert.False(Directory.Exists(dir));
        Assert.Empty(log);
    }

    [Fact]
    public void Tree_containing_a_link_is_left_alone_and_the_target_survives()
    {
        if (!CanLink)
        {
            return;
        }
        var target = Path.Combine(_root, "target");
        Directory.CreateDirectory(target);
        File.WriteAllText(Path.Combine(target, "keep"), "x");
        var dir = Path.Combine(_root, "AppData", "Local", "Nexus");
        Directory.CreateDirectory(dir);
        Directory.CreateSymbolicLink(Path.Combine(dir, "link"), target);
        var log = new List<string>();

        Assert.False(UserDataSafety.TryDeleteUserDir(dir, log.Add));

        Assert.True(Directory.Exists(dir));
        Assert.True(File.Exists(Path.Combine(target, "keep")));
        Assert.Single(log);
    }

    [Fact]
    public void Dir_that_is_itself_a_link_or_sits_behind_one_is_refused()
    {
        if (!CanLink)
        {
            return;
        }
        var target = Path.Combine(_root, "target", "Nexus");
        Directory.CreateDirectory(target);
        var local = Path.Combine(_root, "AppData", "Local");
        Directory.CreateDirectory(Path.GetDirectoryName(local)!);
        Directory.CreateSymbolicLink(local, Path.Combine(_root, "target"));

        Assert.False(UserDataSafety.TryDeleteUserDir(Path.Combine(local, "Nexus"), _ => { }));
        Assert.True(Directory.Exists(target));
    }

    [Fact]
    public void Missing_dir_is_not_an_error()
        => Assert.True(UserDataSafety.TryDeleteUserDir(Path.Combine(_root, "none"), _ => { }));
}
