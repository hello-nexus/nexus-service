using System.IO;
using Nexus.Service.Migration;

namespace Nexus.Service.Tests.Migration;

/// <summary>
/// The silent-uninstall command line the service hands Nexus 2's NSIS
/// uninstaller, and the trust rules on the ARP values it is built from.
/// Running it is Windows-only; what gets run is not.
/// </summary>
public sealed class Nexus2UninstallRulesTests
{
    private static readonly string Root = Path.Combine(Path.GetFullPath(Path.GetTempPath()), "AppData", "Local", "Programs", "HYTE Nexus");
    private static readonly string Exe = Path.Combine(Root, "Uninstall HYTE Nexus.exe");

    [Fact]
    public void Quiet_string_wins_and_gets_the_in_place_marker()
    {
        var result = Nexus2UninstallRules.Compose($"\"{Exe}\" /currentuser /S", $"\"{Exe}\" /currentuser");

        Assert.NotNull(result);
        Assert.Equal(Exe, result!.Value.Exe);
        Assert.Equal($"/currentuser /S _?={Root}", result.Value.Arguments);
        Assert.Equal(Root, result.Value.Root);
    }

    [Fact]
    public void Plain_uninstall_string_gets_silent_flag_appended()
    {
        var result = Nexus2UninstallRules.Compose(null, $"\"{Exe}\" /allusers");

        Assert.NotNull(result);
        Assert.Equal($"/allusers /S _?={Root}", result!.Value.Arguments);
    }

    [Fact]
    public void Only_the_install_mode_switch_comes_from_the_hive()
    {
        var result = Nexus2UninstallRules.Compose($"\"{Exe}\" /currentuser /S /D=C:\\elsewhere --updated", null);

        Assert.Equal($"/currentuser /S _?={Root}", result!.Value.Arguments);
    }

    [Fact]
    public void In_place_marker_is_never_quoted()
    {
        // NSIS reads everything after _?= verbatim and breaks on a quote, so a
        // root with spaces still goes bare.
        var result = Nexus2UninstallRules.Compose($"\"{Exe}\" /S", null);

        Assert.DoesNotContain("\"", result!.Value.Arguments);
        Assert.Contains(" ", Root);
    }

    [Fact]
    public void No_uninstall_string_means_nothing_to_run()
    {
        Assert.Null(Nexus2UninstallRules.Compose(null, null));
        Assert.Null(Nexus2UninstallRules.Compose("  ", ""));
    }

    [Theory]
    [InlineData("AppData/Local/Programs/HYTE Nexus", "Uninstall.exe")]
    [InlineData("AppData/Local/Programs/HYTE Nexus", "cmd.exe")]
    [InlineData("AppData/Local/Programs", "Uninstall HYTE Nexus.exe")]
    [InlineData("Downloads/HYTE Nexus", "Uninstall HYTE Nexus.exe")]
    [InlineData("HYTE Nexus", "Uninstall HYTE Nexus.exe")]
    public void Refuses_any_exe_that_is_not_the_uninstaller_in_an_install_root(string dir, string file)
    {
        var exe = Path.Combine(Path.GetFullPath(Path.GetTempPath()), dir.Replace('/', Path.DirectorySeparatorChar), file);

        Assert.False(Nexus2UninstallRules.IsUninstallerPath(exe));
        Assert.Null(Nexus2UninstallRules.Compose($"\"{exe}\" /S", null));
    }

    [Fact]
    public void Accepts_the_all_users_install_root_and_ignores_case()
    {
        var machine = Path.Combine(Path.GetFullPath(Path.GetTempPath()), "Program Files", "HYTE Nexus", "Uninstall HYTE Nexus.exe");

        Assert.True(Nexus2UninstallRules.IsUninstallerPath(machine));
        Assert.True(Nexus2UninstallRules.IsUninstallerPath(Exe.ToUpperInvariant()));
    }

    [Fact]
    public void Trusts_only_the_exact_signer_names_nexus2_shipped_under()
    {
        Assert.True(Nexus2UninstallRules.IsTrustedSigner("American Future Technology Corp."));
        Assert.True(Nexus2UninstallRules.IsTrustedSigner("HYTE"));
        Assert.False(Nexus2UninstallRules.IsTrustedSigner("HYTE Fake Ltd"));
        Assert.False(Nexus2UninstallRules.IsTrustedSigner("Hytek"));
        Assert.False(Nexus2UninstallRules.IsTrustedSigner("Contoso Ltd"));
        Assert.False(Nexus2UninstallRules.IsTrustedSigner(""));
    }
}
