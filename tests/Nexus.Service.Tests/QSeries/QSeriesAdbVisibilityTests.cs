using System;
using System.Linq;
using AdvancedSharpAdbClient.Models;
using Nexus.Service.Devices;
using Nexus.Service.QSeries;
using Xunit;

namespace Nexus.Service.Tests.QSeries;

public class SetHomeActivityTookTests
{
    [Fact]
    public void Success_output_took()
    {
        Assert.True(QSeriesPortWatcher.SetHomeActivityTook("Success"));
        Assert.True(QSeriesPortWatcher.SetHomeActivityTook("success"));
    }

    [Fact]
    public void Early_boot_service_missing_did_not_take()
    {
        Assert.False(QSeriesPortWatcher.SetHomeActivityTook("cmd: Can't find service: package"));
    }

    [Fact]
    public void Empty_output_did_not_take()
    {
        Assert.False(QSeriesPortWatcher.SetHomeActivityTook(""));
    }
}

public class IsMediaTekInstanceIdTests
{
    [Fact]
    public void Qseries_panel_instance_matches()
    {
        Assert.True(QSeriesPortWatcher.IsMediaTekInstanceId(@"USB\VID_0E8D&PID_201C\0123456789ABCDEF"));
        Assert.True(QSeriesPortWatcher.IsMediaTekInstanceId(@"usb\vid_0e8d&pid_2048\0123456789ABCDEF"));
    }

    [Fact]
    public void Non_mediatek_instance_does_not_match()
    {
        Assert.False(QSeriesPortWatcher.IsMediaTekInstanceId(@"USB\VID_18D1&PID_4EE7\SOMEPHONE01"));
        Assert.False(QSeriesPortWatcher.IsMediaTekInstanceId(""));
    }
}

public class BuildAdbVisibilitySignatureTests
{
    [Fact]
    public void Empty_adb_list_with_vendor_bound_devnode()
    {
        // The field case: adb sees nothing while PnP holds a monolithic
        // MediaTek bind - the one-line signature names the misbinding and
        // the INF to remove.
        var signature = QSeriesPortWatcher.BuildAdbVisibilitySignature(
            new DeviceData[0],
            new[]
            {
                new UsbDeviceEntry
                {
                    VendorId = 0x0E8D, ProductId = 0x201C, Name = "HYTE Q60 Display",
                    Manufacturer = "MediaTek", Class = "AndroidUsbDeviceClass", Driver = "oem42.inf",
                },
            });

        Assert.Equal(
            "adb=[] mediatek-pnp=[HYTE Q60 Display pid=201C class=AndroidUsbDeviceClass mfr=MediaTek inf=oem42.inf]",
            signature);
    }

    [Fact]
    public void Offline_device_includes_state_and_omits_empty_model()
    {
        var signature = QSeriesPortWatcher.BuildAdbVisibilitySignature(
            new[] { new DeviceData { Serial = "0123456789ABCDEF", State = DeviceState.Offline } },
            new UsbDeviceEntry[0]);

        Assert.Equal("adb=[0123456789ABCDEF(Offline)] mediatek-pnp=[]", signature);
    }

    [Fact]
    public void Online_device_with_model_and_empty_serial_filtered()
    {
        var signature = QSeriesPortWatcher.BuildAdbVisibilitySignature(
            new[]
            {
                new DeviceData { Serial = "", State = DeviceState.Offline },
                new DeviceData { Serial = "ABC", State = DeviceState.Unauthorized, Model = "HYTE_Q60_Display" },
            },
            new UsbDeviceEntry[0]);

        Assert.Equal("adb=[ABC(Unauthorized,HYTE_Q60_Display)] mediatek-pnp=[]", signature);
    }
}

public class WindowsTimeZoneMapTests
{
    // The service publishes with InvariantGlobalization, where
    // TimeZoneInfo.TryConvertWindowsIdToIanaId returns false for every Windows id
    // - which silently skipped `cmd alarm set-timezone` on every Windows host and
    // left field panels on their factory zone.
    [Theory]
    [InlineData("India Standard Time", "Asia/Calcutta")]
    [InlineData("Pacific Standard Time", "America/Los_Angeles")]
    [InlineData("GMT Standard Time", "Europe/London")]
    [InlineData("W. Europe Standard Time", "Europe/Berlin")]
    [InlineData("UTC", "Etc/UTC")]
    public void Maps_windows_ids_to_iana(string windowsId, string expected)
    {
        Assert.Equal(expected, WindowsTimeZoneMap.ToIana(windowsId));
    }

    [Fact]
    public void Unknown_id_is_null_so_the_panel_zone_is_left_alone()
    {
        Assert.Null(WindowsTimeZoneMap.ToIana("Not A Real Standard Time"));
    }

    [Fact]
    public void No_value_is_a_windows_registry_id()
    {
        // `cmd alarm set-timezone` only takes IANA; a Windows id reaching the
        // panel is the bug this map exists to close.
        foreach (var windowsId in new[] { "India Standard Time", "UTC", "Tokyo Standard Time" })
        {
            Assert.DoesNotContain("Standard Time", WindowsTimeZoneMap.ToIana(windowsId)!, StringComparison.Ordinal);
        }
    }
}

public class HomeChooserAndTaskTests
{
    // Real `dumpsys activity activities | grep -E "\* Task\{"` output from a Q60
    // (MT8167 BSP, Android 11) while qshell was stacked on the launcher chooser.
    private const string Dump = """
    * Task{f48decd #20 visible=true type=standard mode=fullscreen translucent=false A=10064:com.hellonexus.qshell U=0 StackId=20 sz=1}
    * Task{b6a066f #18 visible=true type=standard mode=fullscreen translucent=true I=android/com.android.internal.app.ResolverActivity U=0 StackId=18 sz=1}
      * Task{f48decd #20 visible=true type=standard mode=fullscreen translucent=false A=10064:com.hellonexus.qshell U=0 StackId=20 sz=1}
      * Task{b6a066f #18 visible=true type=standard mode=fullscreen translucent=true I=android/com.android.internal.app.ResolverActivity U=0 StackId=18 sz=1}
      * Task{9278be3 #17 visible=true type=home mode=fullscreen translucent=true ?? U=0 StackId=17 sz=0}
      * Task{d785ee4 #3 visible=false type=undefined mode=split-screen-primary translucent=true ?? U=0 StackId=3 sz=0}
    """;

    [Fact]
    public void Chooser_focus_detected()
    {
        Assert.True(QSeriesPortWatcher.IsChooserFocus(
            "  mCurrentFocus=Window{1a2b3c u0 android/com.android.internal.app.ResolverActivity}"));
    }

    [Fact]
    public void Qshell_focus_is_not_a_chooser()
    {
        Assert.False(QSeriesPortWatcher.IsChooserFocus(
            "  mCurrentFocus=Window{1a2b3c u0 com.hellonexus.qshell/com.hellonexus.qshell.MainActivity}"));
        Assert.False(QSeriesPortWatcher.IsChooserFocus(""));
    }

    [Fact]
    public void Parses_root_tasks_once_each()
    {
        var tasks = QSeriesPortWatcher.ParsePanelTasks(Dump);
        Assert.Equal(new[] { 20, 18, 17, 3 }, tasks.Select(t => t.Id).ToArray());
        var qshell = tasks.Single(t => t.Id == 20);
        Assert.Equal(("standard", "com.hellonexus.qshell", 20, 1), (qshell.Type, qshell.Component, qshell.StackId, qshell.Size));
        var home = tasks.Single(t => t.Id == 17);
        Assert.Equal(("home", "", 0), (home.Type, home.Component, home.Size));
    }

    [Fact]
    public void Zombie_chooser_and_standard_qshell_are_stale_and_the_empty_home_task_is_not_qshell_home()
    {
        var tasks = QSeriesPortWatcher.ParsePanelTasks(Dump);
        Assert.Equal(new[] { 20, 18 }, tasks.Where(QSeriesPortWatcher.IsStaleStandardTask).Select(t => t.Id).ToArray());
        Assert.DoesNotContain(tasks, QSeriesPortWatcher.IsQshellHomeTask);
    }

    [Fact]
    public void Qshell_in_the_home_task_is_recognised_and_not_stale()
    {
        var tasks = QSeriesPortWatcher.ParsePanelTasks(
            "    * Task{9278be3 #17 visible=true type=home mode=fullscreen translucent=false A=10064:com.hellonexus.qshell U=0 StackId=17 sz=1}");
        Assert.Single(tasks);
        Assert.True(QSeriesPortWatcher.IsQshellHomeTask(tasks[0]));
        Assert.False(QSeriesPortWatcher.IsStaleStandardTask(tasks[0]));
    }
}
