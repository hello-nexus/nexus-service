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

    private static readonly string Tail = Path.Combine("AppData", "Local", "Nexus", "logs");

    private static bool Under(string file, string profile)
    {
        using var stream = new FileStream(file, FileMode.Open, FileAccess.Read);
        return UserDataSafety.HandleIsUnder(stream.SafeFileHandle, UserDataSafety.ExpectedFinalPrefix(profile, Tail));
    }

    [Fact]
    public void Own_file_under_the_profile_logs_dir_is_accepted_and_a_sibling_dir_is_not()
    {
        var profile = Path.Combine(_root, "Users", "p");
        var logs = Path.Combine(profile, Tail);
        var sibling = Path.Combine(profile, "AppData", "Local", "Nexus", "logs2");
        Directory.CreateDirectory(logs);
        Directory.CreateDirectory(sibling);
        File.WriteAllText(Path.Combine(logs, "own.log"), "a");
        File.WriteAllText(Path.Combine(sibling, "x.log"), "b");

        Assert.True(Under(Path.Combine(logs, "own.log"), profile));
        // The final-path check only exists on Windows; elsewhere the reparse-point checks are the guard.
        Assert.Equal(!OperatingSystem.IsWindows(), Under(Path.Combine(sibling, "x.log"), profile));
    }

    [Theory]
    [InlineData("logs")]
    [InlineData("Nexus")]
    public void Files_reached_through_a_swapped_in_link_are_rejected(string swapped)
    {
        var outside = Path.Combine(_root, "outside");
        Directory.CreateDirectory(Path.Combine(outside, "logs"));
        File.WriteAllText(Path.Combine(outside, "logs", "stolen.log"), "x");
        File.WriteAllText(Path.Combine(outside, "stolen.log"), "x");

        var profile = Path.Combine(_root, "Users", "p");
        var nexus = Path.Combine(profile, "AppData", "Local", "Nexus");
        Directory.CreateDirectory(swapped == "logs" ? nexus : Path.GetDirectoryName(nexus)!);
        var link = swapped == "logs" ? Path.Combine(nexus, "logs") : nexus;
        TestLinks.CreateDirLink(link, outside);
        // "logs" swapped: logs -> outside. "Nexus" swapped: Nexus -> outside, so logs\stolen.log resolves to outside\logs\stolen.log.
        var file = Path.Combine(profile, Tail, "stolen.log");

        Assert.True(UserDataSafety.ChainHasReparsePoint(Path.Combine(profile, Tail), 4));
        Assert.Equal(!OperatingSystem.IsWindows(), Under(file, profile));
    }

    [Fact]
    public void Short_8dot3_profile_path_resolves_to_the_same_prefix()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }
        var profile = Path.Combine(_root, "Users", "a-long-profile-name");
        var logs = Path.Combine(profile, Tail);
        Directory.CreateDirectory(logs);
        File.WriteAllText(Path.Combine(logs, "own.log"), "a");
        var buffer = new System.Text.StringBuilder(512);
        if (GetShortPathNameW(profile, buffer, 512) == 0 || string.Equals(buffer.ToString(), profile, StringComparison.OrdinalIgnoreCase))
        {
            return; // 8.3 name generation is off on this volume
        }

        using var stream = new FileStream(Path.Combine(logs, "own.log"), FileMode.Open, FileAccess.Read);
        Assert.True(UserDataSafety.HandleIsUnder(stream.SafeFileHandle, UserDataSafety.ExpectedFinalPrefix(buffer.ToString(), Tail)));
    }

    [System.Runtime.InteropServices.DllImport("kernel32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    private static extern uint GetShortPathNameW(string path, System.Text.StringBuilder buffer, uint size);
}
