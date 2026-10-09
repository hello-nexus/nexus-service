using Nexus.Service.Diagnostics.EventLog;
using Xunit;

namespace Nexus.Service.Tests.Diagnostics;

/// <summary>
/// Fixtures for whea/kernelPower41/nvlddmkm/appError/wer1001 are verbatim
/// EvtRenderEventXml captures from the Y70 test PC
/// (.deep-build/diagnostics-event-samples.txt). Disk/Display(4101)/
/// MemoryDiagnostics fixtures are synthetic: those sources had zero real
/// captures on the bench box. The bugcheck, Display-4125, and nvlddmkm-153
/// fixtures are built from the field shapes recorded for the T1 box in
/// .deep-build/session-20260707-diagnostics-app.md (param1/param2 format,
/// Level-4 Display event, positional nvlddmkm Data) rather than a captured
/// XML blob, since none was available to embed verbatim.
/// </summary>
public class DiagnosticsEventLogTests
{
    private const string WheaXml =
        "<Event xmlns='http://schemas.microsoft.com/win/2004/08/events/event'><System><Provider Name='Microsoft-Windows-WHEA-Logger' Guid='{c26c4f3c-3f66-4e99-8f8a-39405cfed220}'/><EventID>3</EventID><Version>0</Version><Level>4</Level><Task>0</Task><Opcode>0</Opcode><Keywords>0x8000000000000002</Keywords><TimeCreated SystemTime='2026-07-06T23:54:42.8956770Z'/><EventRecordID>39714</EventRecordID><Correlation ActivityID='{37bbe2e1-b016-43da-bbd0-fc6b43fdec1a}'/><Execution ProcessID='4308' ThreadID='5100'/><Channel>System</Channel><Computer>HyteY70</Computer><Security UserID='S-1-5-19'/></System><EventData><Data Name='Length'>316</Data><Data Name='RawData'>435045520101FFFFFFFF010003000000010000003C0100001F36100006071A15A2FE0B8573674F9A97855811224BFA4F00000000000000000000000000000000A2FE0B8573674F9A97855811224BFA4F66A4613D40AB9A40A698F362D464B38F0000000000000000000000000000000000000000000000000000000000000000C80000007400000000010000010000002F1CA4939FA0C2E7AC1FF2488F03EEC30000000000000000000000000000000003000000000000000000000000000000000000000000000007010100000000002F1CA4939FA0C2E7AC1FF2488F03EEC374000000560065006E00480077002800390033004100340031004300320046002D0041003000390046002D0045003700430032002D0041004300310046002D0046003200340038003800460030003300450045004300330029000000</Data></EventData></Event>";

    private const string KernelPower41Xml =
        "<Event xmlns='http://schemas.microsoft.com/win/2004/08/events/event'><System><Provider Name='Microsoft-Windows-Kernel-Power' Guid='{331c3b3a-2005-44c2-ac5e-77220c37d6b4}'/><EventID>41</EventID><Version>10</Version><Level>1</Level><Task>63</Task><Opcode>0</Opcode><Keywords>0x8000400000000002</Keywords><TimeCreated SystemTime='2026-07-06T23:54:33.6351212Z'/><EventRecordID>39637</EventRecordID><Correlation/><Execution ProcessID='4' ThreadID='8'/><Channel>System</Channel><Computer>HyteY70</Computer><Security UserID='S-1-5-18'/></System><EventData><Data Name='BugcheckCode'>0</Data><Data Name='BugcheckParameter1'>0x0</Data><Data Name='BugcheckParameter2'>0x0</Data><Data Name='BugcheckParameter3'>0x0</Data><Data Name='BugcheckParameter4'>0x0</Data><Data Name='SleepInProgress'>0</Data><Data Name='PowerButtonTimestamp'>134278556317579862</Data><Data Name='BootAppStatus'>0</Data><Data Name='Checkpoint'>0</Data><Data Name='ConnectedStandbyInProgress'>false</Data><Data Name='SystemSleepTransitionsToOn'>0</Data><Data Name='CsEntryScenarioInstanceId'>0</Data><Data Name='BugcheckInfoFromEFI'>false</Data><Data Name='CheckpointStatus'>0</Data><Data Name='CsEntryScenarioInstanceIdV2'>0</Data><Data Name='LongPowerButtonPressDetected'>false</Data><Data Name='LidReliability'>false</Data><Data Name='InputSuppressionState'>0</Data><Data Name='PowerButtonSuppressionState'>0</Data><Data Name='LidState'>3</Data><Data Name='WHEABootErrorCount'>0</Data></EventData></Event>";

    private const string NvlddmkmXml =
        "<Event xmlns='http://schemas.microsoft.com/win/2004/08/events/event'><System><Provider Name='nvlddmkm'/><EventID Qualifiers='49322'>13</EventID><Version>0</Version><Level>2</Level><Task>0</Task><Opcode>0</Opcode><Keywords>0x80000000000000</Keywords><TimeCreated SystemTime='2026-06-16T14:01:07.0419487Z'/><EventRecordID>31592</EventRecordID><Correlation/><Execution ProcessID='4' ThreadID='20388'/><Channel>System</Channel><Computer>HyteY70</Computer><Security/></System><EventData><Data>\\Device\\Video3</Data><Data>Graphics FECS Exception: Logging error 0x2</Data><Binary>0000000002003000000000000D00AAC0000000000000000000000000000000000000000000000000</Binary></EventData></Event>";

    private const string AppErrorXml =
        "<Event xmlns='http://schemas.microsoft.com/win/2004/08/events/event'><System><Provider Name='Application Error' Guid='{a0e9b465-b939-57d7-b27d-95d8e925ff57}'/><EventID>1000</EventID><Version>0</Version><Level>2</Level><Task>100</Task><Opcode>0</Opcode><Keywords>0x8000000000000000</Keywords><TimeCreated SystemTime='2026-07-07T00:47:21.2335600Z'/><EventRecordID>90259</EventRecordID><Correlation/><Execution ProcessID='42484' ThreadID='40528'/><Channel>Application</Channel><Computer>HyteY70</Computer><Security UserID='S-1-5-18'/></System><EventData><Data Name='AppName'>adb.exe</Data><Data Name='AppVersion'>0.0.0.0</Data><Data Name='AppTimeStamp'>0bf586cc</Data><Data Name='ModuleName'>ucrtbase.dll</Data><Data Name='ModuleVersion'>10.0.26100.8521</Data><Data Name='ModuleTimeStamp'>ac13ff6d</Data><Data Name='ExceptionCode'>c0000409</Data><Data Name='FaultingOffset'>0002da71</Data><Data Name='ProcessId'>0x3ef8</Data><Data Name='ProcessCreationTime'>0x1dd0da2d4239567</Data><Data Name='AppPath'>C:\\Program Files\\Nexus\\tools\\adb\\adb.exe</Data><Data Name='ModulePath'>C:\\WINDOWS\\System32\\ucrtbase.dll</Data><Data Name='IntegratorReportId'>718aedf8-a6d5-4e22-9e6c-5087dcdb03fc</Data><Data Name='PackageFullName'></Data><Data Name='PackageRelativeAppId'></Data></EventData></Event>";

    // AppErrorXml with the app, faulting module and exception code swapped.
    private static string AppError(string app, string module) => AppErrorXml
        .Replace("<Data Name='AppName'>adb.exe</Data>", $"<Data Name='AppName'>{app}</Data>")
        .Replace("<Data Name='ModuleName'>ucrtbase.dll</Data>", $"<Data Name='ModuleName'>{module}</Data>")
        .Replace("<Data Name='ExceptionCode'>c0000409</Data>", "<Data Name='ExceptionCode'>c0000005</Data>");

    [Fact]
    public void A_Nexus_fault_in_nvml_is_an_info_driver_restart_not_an_app_crash()
    {
        var incident = EventXmlParser.Parse(AppError("Nexus.exe", "nvml.dll"));

        Assert.NotNull(incident);
        Assert.Equal("driverRestart", incident!.Source);
        Assert.Equal("info", incident.Severity);
        Assert.Equal("Nexus restarted during an NVIDIA driver update", incident.Title);
        Assert.Equal("Nexus.exe", incident.App!.Name);
    }

    [Theory]
    [InlineData("Nexus.exe", "ucrtbase.dll")]
    [InlineData("game.exe", "nvml.dll")]
    public void Other_crashes_stay_app_crash_warnings(string app, string module)
    {
        var incident = EventXmlParser.Parse(AppError(app, module));

        Assert.NotNull(incident);
        Assert.Equal("appCrash", incident!.Source);
        Assert.Equal("warning", incident.Severity);
    }

    private const string Wer1001LiveKernelXml =
        @"<Event xmlns='http://schemas.microsoft.com/win/2004/08/events/event'><System><Provider Name='Windows Error Reporting' Guid='{0ead09bd-2157-539a-8d6d-c87f95b64d70}'/><EventID>1001</EventID><Version>0</Version><Level>4</Level><Task>0</Task><Opcode>0</Opcode><Keywords>0x8000000000000000</Keywords><TimeCreated SystemTime='2026-07-08T04:42:03.2990784Z'/><EventRecordID>90573</EventRecordID><Correlation/><Execution ProcessID='24752' ThreadID='42724'/><Channel>Application</Channel><Computer>HyteY70</Computer><Security UserID='S-1-5-18'/></System><EventData><Data Name='Bucket'></Data><Data Name='BucketType'>0</Data><Data Name='EventName'>LiveKernelEvent</Data><Data Name='Response'>Not available</Data><Data Name='CabId'>0</Data><Data Name='P1'>1b8</Data><Data Name='P2'>a</Data><Data Name='P3'>0</Data><Data Name='P4'>0</Data><Data Name='P5'>0</Data><Data Name='P6'>10_0_26200</Data><Data Name='P7'>0_0</Data><Data Name='P8'>768_1</Data><Data Name='P9'></Data><Data Name='P10'></Data><Data Name='AttachedFiles'>
\\?\C:\WINDOWS\LiveKernelReports\WATCHDOG4400\WATCHDOG4400-20260704-1846.dmp
\\?\C:\WINDOWS\SystemTemp\WER-10984-0.sysdata.xml
\\?\C:\ProgramData\Microsoft\Windows\WER\Temp\WER.38c5a648-a9cd-4c05-b3a0-6ea27dc5cf73.tmp.WERInternalMetadata.xml
\\?\C:\ProgramData\Microsoft\Windows\WER\Temp\WER.fb0a6b2e-19ba-4a95-8912-aac3c2c2f702.tmp.csv
\\?\C:\ProgramData\Microsoft\Windows\WER\Temp\WER.ba183ef7-40b7-404f-bc33-7e1dc16c89ac.tmp.txt
\\?\C:\ProgramData\Microsoft\Windows\WER\Temp\WER.4cb06111-6b94-4567-bf7a-5bccc9f28749.tmp.xml</Data><Data Name='StorePath'>\\?\C:\ProgramData\Microsoft\Windows\WER\ReportQueue\Kernel_1b8_8dd2bee64833d969274b897e7444ab4a6d4f908_00000000_661a723d-f1a0-4806-9028-cdaaac87d9e5</Data><Data Name='AnalysisSymbol'></Data><Data Name='Rechecking'>0</Data><Data Name='ReportId'>661a723d-f1a0-4806-9028-cdaaac87d9e5</Data><Data Name='ReportStatus'>2049</Data><Data Name='HashedBucket'></Data><Data Name='CabGuid'>0</Data></EventData></Event>";

    [Fact]
    public void Parses_real_whea_event()
    {
        var incident = EventXmlParser.Parse(WheaXml);

        Assert.NotNull(incident);
        Assert.Equal("System/39714", incident!.Id);
        Assert.Equal("whea", incident.Source);
        Assert.Equal("warning", incident.Severity);
        Assert.Equal("Hardware error event 3", incident.Title);
        Assert.Equal("WHEA-Logger event 3.", incident.Detail);
        Assert.Null(incident.App);
        Assert.Empty(incident.Data);
        Assert.Equal(2026, incident.TimeUtc.Year);
        Assert.Equal(7, incident.TimeUtc.Month);
        Assert.Equal(6, incident.TimeUtc.Day);
        Assert.Equal(23, incident.TimeUtc.Hour);
        Assert.Equal(54, incident.TimeUtc.Minute);
        Assert.Equal(42, incident.TimeUtc.Second);
        Assert.Equal(DateTimeKind.Utc, incident.TimeUtc.Kind);
    }

    [Fact]
    public void Parses_real_kernel_power_41_clean_shutdown()
    {
        var incident = EventXmlParser.Parse(KernelPower41Xml);

        Assert.NotNull(incident);
        Assert.Equal("System/39637", incident!.Id);
        Assert.Equal("dirtyShutdown", incident.Source);
        Assert.Equal("warning", incident.Severity);
        Assert.Equal("Unexpected power loss or hard lock", incident.Title);
        Assert.Empty(incident.Data);
    }

    [Fact]
    public void Kernel_power_41_with_nonzero_bugcheck_is_critical()
    {
        var xml = KernelPower41Xml.Replace("<Data Name='BugcheckCode'>0</Data>", "<Data Name='BugcheckCode'>26</Data>");

        var incident = EventXmlParser.Parse(xml);

        Assert.NotNull(incident);
        Assert.Equal("critical", incident!.Severity);
        Assert.Equal("Unexpected shutdown with bugcheck", incident.Title);
        Assert.Equal("0x1a", incident.Data["bugcheckCode"]);
    }

    [Fact]
    public void Parses_real_nvlddmkm_event()
    {
        var incident = EventXmlParser.Parse(NvlddmkmXml);

        Assert.NotNull(incident);
        Assert.Equal("System/31592", incident!.Id);
        Assert.Equal("gpuDriver", incident.Source);
        Assert.Equal("warning", incident.Severity);
        Assert.Equal(@"\Device\Video3 Graphics FECS Exception: Logging error 0x2", incident.Detail);
    }

    [Fact]
    public void Parses_real_app_crash_event()
    {
        var incident = EventXmlParser.Parse(AppErrorXml);

        Assert.NotNull(incident);
        Assert.Equal("Application/90259", incident!.Id);
        Assert.Equal("appCrash", incident.Source);
        Assert.NotNull(incident.App);
        Assert.Equal("adb.exe", incident.App!.Name);
        Assert.Equal(@"C:\Program Files\Nexus\tools\adb\adb.exe", incident.App.Path);
        Assert.Equal("c0000409", incident.App.ExceptionCode);
        Assert.Equal("ucrtbase.dll", incident.App.FaultingModule);
        Assert.False(incident.App.IsGame);
        Assert.Equal("0.0.0.0", incident.Data["appVersion"]);
        Assert.Equal(@"C:\WINDOWS\System32\ucrtbase.dll", incident.Data["modulePath"]);
    }

    [Fact]
    public void Parses_real_wer1001_live_kernel_event()
    {
        var incident = EventXmlParser.Parse(Wer1001LiveKernelXml);

        Assert.NotNull(incident);
        Assert.Equal("Application/90573", incident!.Id);
        Assert.Equal("liveKernel", incident.Source);
        Assert.Equal("critical", incident.Severity);
        Assert.Equal("1b8", incident.Data["p1"]);
        Assert.Equal("a", incident.Data["p2"]);
    }

    [Fact]
    public void Wer1001_without_live_kernel_event_name_is_ignored()
    {
        var xml = Wer1001LiveKernelXml.Replace("<Data Name='EventName'>LiveKernelEvent</Data>", "<Data Name='EventName'>AppHangB1</Data>");

        Assert.Null(EventXmlParser.Parse(xml));
    }

    [Theory]
    [InlineData(3, 4, "warning", "Hardware error event 3")]
    [InlineData(17, 2, "critical", "Corrected PCIe/MCE hardware error")]
    [InlineData(19, 3, "warning", "Corrected PCIe/MCE hardware error")]
    [InlineData(18, 1, "critical", "Fatal hardware error")]
    [InlineData(46, 4, "warning", "Fatal hardware error")]
    [InlineData(47, 4, "warning", "Corrected memory error")]
    [InlineData(99, 4, "warning", "Hardware error event 99")]
    public void Whea_severity_is_level_driven_and_title_is_id_driven(int id, int level, string expectedSeverity, string expectedTitle)
    {
        var xml = BuildWheaXml(id, level, recordId: 1);

        var incident = EventXmlParser.Parse(xml);

        Assert.NotNull(incident);
        Assert.Equal("whea", incident!.Source);
        Assert.Equal(expectedSeverity, incident.Severity);
        Assert.Equal(expectedTitle, incident.Title);
    }

    [Fact]
    public void Whea_event_without_eventdata_still_parses()
    {
        const string xml =
            "<Event xmlns='http://schemas.microsoft.com/win/2004/08/events/event'><System><Provider Name='Microsoft-Windows-WHEA-Logger'/><EventID>3</EventID><Level>4</Level><TimeCreated SystemTime='2026-01-01T00:00:00.0000000Z'/><EventRecordID>1</EventRecordID><Channel>System</Channel><Computer>Test</Computer></System></Event>";

        var incident = EventXmlParser.Parse(xml);

        Assert.NotNull(incident);
        Assert.Equal("whea", incident!.Source);
    }

    [Fact]
    public void Disk_event_7_is_watched()
    {
        const string xml =
            "<Event xmlns='http://schemas.microsoft.com/win/2004/08/events/event'><System><Provider Name='disk'/><EventID>7</EventID><Version>0</Version><Level>2</Level><Task>0</Task><Opcode>0</Opcode><Keywords>0x8080000000000000</Keywords><TimeCreated SystemTime='2026-06-01T08:00:00.0000000Z'/><EventRecordID>5001</EventRecordID><Correlation/><Execution ProcessID='4' ThreadID='8'/><Channel>System</Channel><Computer>HyteY70</Computer><Security/></System></Event>";

        var incident = EventXmlParser.Parse(xml);

        Assert.NotNull(incident);
        Assert.Equal("System/5001", incident!.Id);
        Assert.Equal("disk", incident.Source);
        Assert.Equal("warning", incident.Severity);
        Assert.Equal("Bad block detected on disk", incident.Title);
    }

    [Fact]
    public void Display_tdr_event_is_watched()
    {
        const string xml =
            "<Event xmlns='http://schemas.microsoft.com/win/2004/08/events/event'><System><Provider Name='Display'/><EventID>4101</EventID><Version>0</Version><Level>2</Level><Task>0</Task><Opcode>0</Opcode><Keywords>0x80000000000000</Keywords><TimeCreated SystemTime='2026-06-02T09:30:00.0000000Z'/><EventRecordID>5100</EventRecordID><Correlation/><Execution ProcessID='4' ThreadID='8'/><Channel>System</Channel><Computer>HyteY70</Computer><Security/></System><EventData><Data>nvlddmkm</Data></EventData></Event>";

        var incident = EventXmlParser.Parse(xml);

        Assert.NotNull(incident);
        Assert.Equal("tdr", incident!.Source);
        Assert.Equal("critical", incident.Severity);
    }

    [Fact]
    public void Display_event_below_error_level_is_a_generic_warning()
    {
        const string xml =
            "<Event xmlns='http://schemas.microsoft.com/win/2004/08/events/event'><System><Provider Name='Display'/><EventID>4103</EventID><Version>0</Version><Level>2</Level><Task>0</Task><Opcode>0</Opcode><Keywords>0x80000000000000</Keywords><TimeCreated SystemTime='2026-06-02T09:31:00.0000000Z'/><EventRecordID>5101</EventRecordID><Correlation/><Execution ProcessID='4' ThreadID='8'/><Channel>System</Channel><Computer>HyteY70</Computer><Security/></System></Event>";

        var incident = EventXmlParser.Parse(xml);

        Assert.NotNull(incident);
        Assert.Equal("tdr", incident!.Source);
        Assert.Equal("warning", incident.Severity);
        Assert.Equal("Display error event 4103", incident.Title);
    }

    // Reproduces the id/level/EventData shape of the real Display 4125 event
    // recorded on T1 (session-20260707-diagnostics-app.md).
    [Fact]
    public void Display_event_at_info_level_is_ignored()
    {
        const string xml =
            "<Event xmlns='http://schemas.microsoft.com/win/2004/08/events/event'><System><Provider Name='Display'/><EventID>4125</EventID><Version>0</Version><Level>4</Level><Task>0</Task><Opcode>0</Opcode><Keywords>0x80000000000000</Keywords><TimeCreated SystemTime='2026-06-02T09:32:00.0000000Z'/><EventRecordID>5102</EventRecordID><Correlation/><Execution ProcessID='4' ThreadID='8'/><Channel>System</Channel><Computer>HyteY70</Computer><Security/></System><EventData></EventData></Event>";

        Assert.Null(EventXmlParser.Parse(xml));
    }

    [Theory]
    [InlineData(1201, "info")]
    [InlineData(1202, "critical")]
    public void Memory_diagnostics_results_severity_is_id_driven(int id, string expectedSeverity)
    {
        var xml =
            $"<Event xmlns='http://schemas.microsoft.com/win/2004/08/events/event'><System><Provider Name='Microsoft-Windows-MemoryDiagnostics-Results'/><EventID>{id}</EventID><Version>0</Version><Level>4</Level><Task>0</Task><Opcode>0</Opcode><Keywords>0x8000000000000000</Keywords><TimeCreated SystemTime='2026-06-03T07:00:00.0000000Z'/><EventRecordID>5200</EventRecordID><Correlation/><Execution ProcessID='4' ThreadID='8'/><Channel>System</Channel><Computer>HyteY70</Computer><Security/></System></Event>";

        var incident = EventXmlParser.Parse(xml);

        Assert.NotNull(incident);
        Assert.Equal("memDiag", incident!.Source);
        Assert.Equal(expectedSeverity, incident.Severity);
    }

    // param1/param2/param3 reproduce the field shape T1 recorded for a real
    // BugCheck 1001 event (session-20260707-diagnostics-app.md): param1 is
    // "<code> (<p1>, <p2>, <p3>, <p4>)", param2 is the minidump path, param3
    // is the report GUID.
    [Fact]
    public void Bugcheck_event_extracts_code_and_dump_path_from_param1_param2()
    {
        const string xml =
            "<Event xmlns='http://schemas.microsoft.com/win/2004/08/events/event'><System><Provider Name='Microsoft-Windows-WER-SystemErrorReporting'/><EventID>1001</EventID><Version>0</Version><Level>2</Level><Task>0</Task><Opcode>0</Opcode><Keywords>0x8000000000000000</Keywords><TimeCreated SystemTime='2026-06-04T10:00:00.0000000Z'/><EventRecordID>5300</EventRecordID><Correlation/><Execution ProcessID='4' ThreadID='8'/><Channel>System</Channel><Computer>HyteY70</Computer><Security/></System><EventData><Data Name='param1'>0x000000ef (0xffffb001a2345680, 0xfffff80100000001, 0x0000000000000000, 0x0000000000000001)</Data><Data Name='param2'>C:\\Windows\\MEMORY.DMP</Data><Data Name='param3'>661a723d-f1a0-4806-9028-cdaaac87d9e5</Data></EventData></Event>";

        var incident = EventXmlParser.Parse(xml);

        Assert.NotNull(incident);
        Assert.Equal("bugcheck", incident!.Source);
        Assert.Equal("critical", incident.Severity);
        Assert.Equal("0x000000ef", incident.Data["bugcheckCode"]);
        Assert.Equal(@"C:\Windows\MEMORY.DMP", incident.Data["dumpPath"]);
        Assert.False(incident.Data.ContainsKey("bugcheckParam1"));
    }

    // Reproduces the provider/Qualifiers/positional-Data shape T1 recorded
    // for a real nvlddmkm 153 event (session-20260707-diagnostics-app.md).
    [Fact]
    public void Nvlddmkm_153_joins_positional_data_into_detail()
    {
        const string xml =
            "<Event xmlns='http://schemas.microsoft.com/win/2004/08/events/event'><System><Provider Name='nvlddmkm'/><EventID Qualifiers='49322'>153</EventID><Version>0</Version><Level>2</Level><Task>0</Task><Opcode>0</Opcode><Keywords>0x80000000000000</Keywords><TimeCreated SystemTime='2026-06-16T14:05:00.0000000Z'/><EventRecordID>31600</EventRecordID><Correlation/><Execution ProcessID='4' ThreadID='20388'/><Channel>System</Channel><Computer>HyteY70</Computer><Security/></System><EventData><Data>\\Device\\Video4</Data><Data>Display driver stopped responding and has recovered</Data></EventData></Event>";

        var incident = EventXmlParser.Parse(xml);

        Assert.NotNull(incident);
        Assert.Equal("gpuDriver", incident!.Source);
        Assert.Equal(@"\Device\Video4 Display driver stopped responding and has recovered", incident.Detail);
    }

    [Fact]
    public void Nvlddmkm_event_at_info_level_is_ignored()
    {
        const string xml =
            "<Event xmlns='http://schemas.microsoft.com/win/2004/08/events/event'><System><Provider Name='nvlddmkm'/><EventID Qualifiers='49322'>13</EventID><Version>0</Version><Level>4</Level><Task>0</Task><Opcode>0</Opcode><Keywords>0x80000000000000</Keywords><TimeCreated SystemTime='2026-06-16T14:01:07.0000000Z'/><EventRecordID>31593</EventRecordID><Correlation/><Execution ProcessID='4' ThreadID='20388'/><Channel>System</Channel><Computer>HyteY70</Computer><Security/></System><EventData><Data>\\Device\\Video3</Data><Data>Info-level driver chatter</Data></EventData></Event>";

        Assert.Null(EventXmlParser.Parse(xml));
    }

    [Fact]
    public void Unwatched_provider_returns_null()
    {
        const string xml =
            "<Event xmlns='http://schemas.microsoft.com/win/2004/08/events/event'><System><Provider Name='Some-Other-Provider'/><EventID>1</EventID><Level>4</Level><TimeCreated SystemTime='2026-01-01T00:00:00.0000000Z'/><EventRecordID>1</EventRecordID><Channel>System</Channel><Computer>Test</Computer></System></Event>";

        Assert.Null(EventXmlParser.Parse(xml));
    }

    [Theory]
    [InlineData("")]
    [InlineData("not xml at all")]
    [InlineData("<Event><System>")]
    [InlineData("<Event xmlns='http://schemas.microsoft.com/win/2004/08/events/event'><System></System></Event>")]
    public void Malformed_or_incomplete_xml_returns_null(string xml)
    {
        Assert.Null(EventXmlParser.Parse(xml));
    }

    private static string BuildWheaXml(int id, int level, long recordId) =>
        "<Event xmlns='http://schemas.microsoft.com/win/2004/08/events/event'><System><Provider Name='Microsoft-Windows-WHEA-Logger' Guid='{c26c4f3c-3f66-4e99-8f8a-39405cfed220}'/>"
        + $"<EventID>{id}</EventID><Version>0</Version><Level>{level}</Level><Task>0</Task><Opcode>0</Opcode><Keywords>0x8000000000000002</Keywords>"
        + $"<TimeCreated SystemTime='2026-07-06T23:54:42.8956770Z'/><EventRecordID>{recordId}</EventRecordID><Correlation/><Execution ProcessID='4308' ThreadID='5100'/>"
        + "<Channel>System</Channel><Computer>HyteY70</Computer><Security UserID='S-1-5-19'/></System><EventData><Data Name='Length'>316</Data></EventData></Event>";
}
