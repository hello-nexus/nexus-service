using System.IO;
using Nexus.Service.Update;
using Xunit;

namespace Nexus.Service.Tests.Update;

public sealed class UpdateInstallerTests
{
    // The OTA installer embeds the version in the staged .cmd / log paths and
    // the schtask name, so it validates the tag first. The validator used to
    // accept only "v" + digits/dots, which rejected the "-beta.N" semver
    // prerelease suffix - so no prerelease could ever install via OTA.
    [Theory]
    [InlineData("v3.0.1", true)]
    [InlineData("v80", true)]                  // legacy monotonic tag
    [InlineData("v0.0.0", true)]
    [InlineData("v3.0.1-beta.1", true)]        // semver prerelease (the regression)
    [InlineData("v3.0.10-beta.2", true)]
    [InlineData("v3.0.1-rc.2", true)]
    [InlineData("", false)]
    [InlineData("3.0.1", false)]               // missing "v" prefix
    [InlineData("v", false)]
    [InlineData("vbeta", false)]               // no digit
    [InlineData("v3.0.1/x", false)]            // path separator
    [InlineData("v3.0.1\\x", false)]           // path separator
    [InlineData("v3.0.1 1", false)]            // space
    [InlineData("v3.0.1;rm", false)]           // shell metacharacter
    public void IsValidVersionTag_accepts_semver_including_prerelease(string tag, bool expected)
    {
        Assert.Equal(expected, UpdateInstaller.IsValidVersionTag(tag));
    }

    // The installer launches as SYSTEM, so a marker-supplied path must resolve
    // inside the SYSTEM-locked staging dir. A non-admin who plants a marker
    // pointing at their own payload is rejected here even if the marker is read.
    [Fact]
    public void IsWithinStagingDir_accepts_a_file_inside_the_staging_dir()
    {
        var path = Path.Combine(UpdateDownloader.StagingDir, "Nexus-Setup-v3.0.1.exe");
        Assert.True(UpdateInstaller.IsWithinStagingDir(path));
    }

    [Fact]
    public void IsWithinStagingDir_rejects_a_path_outside_the_staging_dir()
    {
        var outside = Path.Combine(Path.GetTempPath(), "evil.exe");
        Assert.False(UpdateInstaller.IsWithinStagingDir(outside));
    }

    [Fact]
    public void IsWithinStagingDir_rejects_a_traversal_escape()
    {
        var escape = Path.Combine(UpdateDownloader.StagingDir, "..", "evil.exe");
        Assert.False(UpdateInstaller.IsWithinStagingDir(escape));
    }

    [Fact]
    public void IsWithinStagingDir_rejects_a_sibling_prefix_dir()
    {
        // A sibling like "updates-evil" shares the staging dir's string prefix but
        // is a different directory; the separator guard must reject it.
        var sibling = UpdateDownloader.StagingDir + "-evil" + Path.DirectorySeparatorChar + "x.exe";
        Assert.False(UpdateInstaller.IsWithinStagingDir(sibling));
    }

    [Theory]
    [InlineData("")]
    [InlineData(null)]
    public void IsWithinStagingDir_rejects_empty(string? path)
    {
        Assert.False(UpdateInstaller.IsWithinStagingDir(path!));
    }

    // `schtasks /Query /FO CSV /NH` returns the full task path with a leading
    // backslash. The cleanup used a raw StartsWith, which the backslash always
    // defeated, so spent NexusOtaInstall_* tasks were never removed and their
    // leftover trigger re-ran the installer nightly. Match the leaf instead.
    [Theory]
    [InlineData("\\NexusOtaInstall_22056_639183551943660816", true)]  // real CSV form (leading '\')
    [InlineData("NexusOtaInstall_1_2", true)]                          // no folder prefix
    [InlineData("\\Microsoft\\Windows\\SomeTask", false)]
    [InlineData("\\NexusOverlayLaunch_1_2", false)]                    // a different Nexus task, not ours
    [InlineData("\\", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void IsOwnedTaskName_matches_our_tasks_despite_the_leading_backslash(string? csvName, bool expected)
    {
        Assert.Equal(expected, UpdateInstaller.IsOwnedTaskName(csvName!));
    }
}
