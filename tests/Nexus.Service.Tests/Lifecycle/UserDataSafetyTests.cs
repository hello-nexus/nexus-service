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
        var target = Path.Combine(_root, "target");
        Directory.CreateDirectory(target);
        File.WriteAllText(Path.Combine(target, "keep"), "x");
        var dir = Path.Combine(_root, "AppData", "Local", "Nexus");
        Directory.CreateDirectory(dir);
        TestLinks.CreateDirLink(Path.Combine(dir, "link"), target);
        var log = new List<string>();

        Assert.False(UserDataSafety.TryDeleteUserDir(dir, log.Add));

        Assert.True(Directory.Exists(dir));
        Assert.True(File.Exists(Path.Combine(target, "keep")));
        Assert.Single(log);
    }

    [Fact]
    public void Dir_that_is_itself_a_link_or_sits_behind_one_is_refused()
    {
        var target = Path.Combine(_root, "target", "Nexus");
        Directory.CreateDirectory(target);
        var local = Path.Combine(_root, "AppData", "Local");
        Directory.CreateDirectory(Path.GetDirectoryName(local)!);
        TestLinks.CreateDirLink(local, Path.Combine(_root, "target"));

        Assert.False(UserDataSafety.TryDeleteUserDir(Path.Combine(local, "Nexus"), _ => { }));
        Assert.True(Directory.Exists(target));
    }

    [Fact]
    public void Missing_dir_is_not_an_error()
        => Assert.True(UserDataSafety.TryDeleteUserDir(Path.Combine(_root, "none"), _ => { }));

    [Fact]
    public void Handle_reached_through_a_link_is_not_under_the_expected_dir()
    {
        var logs = Path.Combine(_root, "logs");
        var target = Path.Combine(_root, "elsewhere");
        Directory.CreateDirectory(logs);
        Directory.CreateDirectory(target);
        File.WriteAllText(Path.Combine(logs, "own.log"), "a");
        File.WriteAllText(Path.Combine(target, "other.log"), "b");
        var link = Path.Combine(logs, "link");
        TestLinks.CreateDirLink(link, target);

        using var own = new FileStream(Path.Combine(logs, "own.log"), FileMode.Open, FileAccess.Read);
        using var linked = new FileStream(Path.Combine(link, "other.log"), FileMode.Open, FileAccess.Read);

        Assert.True(UserDataSafety.HandleIsUnder(own.SafeFileHandle, logs));
        // The final-path check only exists on Windows; elsewhere the earlier reparse check is the guard.
        Assert.Equal(!OperatingSystem.IsWindows(), UserDataSafety.HandleIsUnder(linked.SafeFileHandle, logs));
    }
}
