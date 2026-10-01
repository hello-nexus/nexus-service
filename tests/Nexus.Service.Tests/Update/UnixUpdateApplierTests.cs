using Nexus.Service.Update;

namespace Nexus.Service.Tests.Update;

public class UnixUpdateApplierTests
{
    [Fact]
    public void BundleFromExe_returns_the_app_for_its_main_executable()
    {
        Assert.Equal("/Applications/Nexus.app",
            UnixUpdateApplier.BundleFromExe("/Applications/Nexus.app/Contents/MacOS/Nexus"));
    }

    [Theory]
    [InlineData("/usr/local/bin/Nexus")]
    [InlineData("/Users/dev/nexus-service/bin/Debug/Nexus")]
    [InlineData("/Applications/Nexus.app/Contents/Resources/Nexus")]
    [InlineData("/tmp/Nexus/Contents/MacOS/Nexus")]
    [InlineData(null)]
    public void BundleFromExe_null_outside_a_bundle(string? exe)
    {
        Assert.Null(UnixUpdateApplier.BundleFromExe(exe));
    }

    [Fact]
    public void CodesignField_reads_team_and_identifier()
    {
        // codesign -dv prints its fields on stderr, one key=value per line.
        const string output =
            "Executable=/Applications/Nexus.app/Contents/MacOS/Nexus\n" +
            "Identifier=com.hellonexus.panel.service\n" +
            "Format=app bundle with Mach-O thin (arm64)\n" +
            "TeamIdentifier=8ZFCKY2SQ9\n";
        Assert.Equal("8ZFCKY2SQ9", UnixUpdateApplier.CodesignField(output, "TeamIdentifier"));
        Assert.Equal("com.hellonexus.panel.service", UnixUpdateApplier.CodesignField(output, "Identifier"));
    }

    [Fact]
    public void CodesignField_null_when_absent()
    {
        Assert.Null(UnixUpdateApplier.CodesignField("Executable=/x\n", "TeamIdentifier"));
    }

    [Fact]
    public void LinuxInstallArgs_runs_install_sh_in_its_own_unit_and_restarts_nexus()
    {
        var args = UnixUpdateApplier.LinuxInstallArgs("/var/lib/nexus/updates/linux-install/nexus/install.sh",
            "/var/lib/nexus/updates/ota-install-v3.0.21.log", "/home/deck");

        Assert.Contains("--unit=nexus-ota-install", args);
        Assert.Contains("--collect", args);
        Assert.Contains("--setenv=HOME=/home/deck", args);
        Assert.Contains("--property=StandardOutput=append:/var/lib/nexus/updates/ota-install-v3.0.21.log", args);
        var command = args[args.IndexOf("-c") + 1];
        Assert.Contains("--update", command);
        Assert.Contains("systemctl start nexus.service", command);
        Assert.Equal("/var/lib/nexus/updates/linux-install/nexus/install.sh", args[^1]);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void LinuxInstallArgs_falls_back_to_root_home(string? home)
    {
        var args = UnixUpdateApplier.LinuxInstallArgs("/x/install.sh", "/x/log", home);
        Assert.Contains("--setenv=HOME=/root", args);
    }

    [Fact]
    public void PayloadFileName_keeps_the_prune_prefix_and_the_platform_extension()
    {
        var name = UpdateDownloader.PayloadFileName("v3.0.21");
        Assert.StartsWith("Nexus-Setup-v3.0.21", name);
        var expected = OperatingSystem.IsMacOS() ? ".dmg" : OperatingSystem.IsLinux() ? ".tar.gz" : ".exe";
        Assert.EndsWith(expected, name);
    }
}
