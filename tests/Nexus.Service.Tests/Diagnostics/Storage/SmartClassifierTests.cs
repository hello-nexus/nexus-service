using System.Collections.Generic;
using Nexus.Service.Diagnostics.Storage;
using Xunit;

namespace Nexus.Service.Tests.Diagnostics.Storage;

public class SmartClassifierTests
{
    private static SmartAttributeSample HealthyAttr(byte id, string name = "") =>
        new(id, name, Current: 200, Worst: 200, Threshold: 0, Raw: 0);

    [Fact]
    public void Clean_ata_drive_is_good_with_no_reasons()
    {
        var input = new SmartClassifierInput(
            StorageBusKind.Sata,
            new List<SmartAttributeSample> { HealthyAttr(5), HealthyAttr(197) },
            Nvme: null,
            VendorHealth: VendorHealthStatus.Good,
            PredictsFailure: false);

        var result = SmartClassifier.Classify(input);

        Assert.Equal(DriveStatus.Good, result.Status);
        Assert.Empty(result.Reasons);
        Assert.Empty(result.FlaggedAttributeIds);
    }

    [Fact]
    public void Pending_sectors_raw_3_is_caution_with_pendingSectors_reason()
    {
        var input = new SmartClassifierInput(
            StorageBusKind.Sata,
            new List<SmartAttributeSample> { new(197, "Current Pending Sector Count", 100, 100, 0, Raw: 3) },
            Nvme: null,
            VendorHealth: null,
            PredictsFailure: null);

        var result = SmartClassifier.Classify(input);

        Assert.Equal(DriveStatus.Caution, result.Status);
        var reason = Assert.Single(result.Reasons);
        Assert.Equal("smart.pendingSectors", reason.Code);
        Assert.Equal(ReasonSeverity.Watch, reason.Severity);
        Assert.Contains((byte)197, result.FlaggedAttributeIds);
    }

    [Fact]
    public void Backblaze_attribute_over_100_raw_escalates_to_act()
    {
        var input = new SmartClassifierInput(
            StorageBusKind.Sata,
            new List<SmartAttributeSample> { new(5, "Reallocated Sectors Count", 90, 90, 0, Raw: 150) },
            Nvme: null,
            VendorHealth: null,
            PredictsFailure: null);

        var result = SmartClassifier.Classify(input);

        var reason = Assert.Single(result.Reasons);
        Assert.Equal("smart.reallocated", reason.Code);
        Assert.Equal(ReasonSeverity.Act, reason.Severity);
        // Act without PredictsFailure or an nvme critical warning maps to Warning, not Bad.
        Assert.Equal(DriveStatus.Warning, result.Status);
    }

    [Fact]
    public void Nvme_bus_does_not_run_ata_backblaze_rules_even_on_id_collision()
    {
        // An NVMe drive should never carry legacy ATA attribute ids, but if DiskInfoToolkit
        // ever surfaced one coincidentally, the Backblaze rule must stay ATA/SATA-only.
        var input = new SmartClassifierInput(
            StorageBusKind.Nvme,
            new List<SmartAttributeSample> { new(5, "Reallocated Sectors Count", 90, 90, 0, Raw: 5) },
            Nvme: null,
            VendorHealth: null,
            PredictsFailure: null);

        var result = SmartClassifier.Classify(input);

        Assert.Empty(result.Reasons);
        Assert.Equal(DriveStatus.Good, result.Status);
    }

    [Fact]
    public void Nvme_critical_warning_bit2_is_act_and_escalates_status_to_bad()
    {
        var nvme = new NvmeHealthLog
        {
            CriticalWarning = 0x04, // bit2: NVM subsystem reliability degraded
            CompositeTemperatureKelvin = 300,
            AvailableSpare = 100,
            AvailableSpareThreshold = 10,
            PercentageUsed = 1,
            DataUnitsReadBytes = 0,
            DataUnitsWrittenBytes = 0,
            PowerCycles = 0,
            PowerOnHours = 0,
            UnsafeShutdowns = 0,
            MediaErrors = 0,
            ErrorInfoLogEntries = 0,
        };
        var input = new SmartClassifierInput(
            StorageBusKind.Nvme, new List<SmartAttributeSample>(), nvme, VendorHealth: null, PredictsFailure: null);

        var result = SmartClassifier.Classify(input);

        var reason = Assert.Single(result.Reasons);
        Assert.Equal("nvme.criticalWarning", reason.Code);
        Assert.Equal(ReasonSeverity.Act, reason.Severity);
        // criticalWarning is one of the two conditions that escalates an act reason to Bad.
        Assert.Equal(DriveStatus.Bad, result.Status);
    }

    [Fact]
    public void Nvme_spare_below_threshold_is_act_and_warning_without_other_escalation()
    {
        var nvme = new NvmeHealthLog
        {
            CriticalWarning = 0,
            CompositeTemperatureKelvin = 300,
            AvailableSpare = 5,
            AvailableSpareThreshold = 10,
            PercentageUsed = 1,
            DataUnitsReadBytes = 0,
            DataUnitsWrittenBytes = 0,
            PowerCycles = 0,
            PowerOnHours = 0,
            UnsafeShutdowns = 0,
            MediaErrors = 0,
            ErrorInfoLogEntries = 0,
        };
        var input = new SmartClassifierInput(
            StorageBusKind.Nvme, new List<SmartAttributeSample>(), nvme, VendorHealth: null, PredictsFailure: null);

        var result = SmartClassifier.Classify(input);

        var reason = Assert.Single(result.Reasons);
        Assert.Equal("nvme.spareLow", reason.Code);
        Assert.Equal(ReasonSeverity.Act, reason.Severity);
        Assert.Equal(DriveStatus.Warning, result.Status);
    }

    [Fact]
    public void Dit_caution_health_status_passes_through_as_watch_vendorHealth_reason()
    {
        var input = new SmartClassifierInput(
            StorageBusKind.Sata, new List<SmartAttributeSample>(), Nvme: null,
            VendorHealth: VendorHealthStatus.Caution, PredictsFailure: null);

        var result = SmartClassifier.Classify(input);

        var reason = Assert.Single(result.Reasons);
        Assert.Equal("smart.vendorHealth", reason.Code);
        Assert.Equal(ReasonSeverity.Watch, reason.Severity);
        Assert.Equal(DriveStatus.Caution, result.Status);
    }

    [Fact]
    public void Dit_warning_or_bad_health_status_is_act_not_duplicated_when_already_flagged()
    {
        // PredictsFailure already adds one smart.vendorHealth reason; DIT's Warning
        // status must not add a second one for the same drive.
        var input = new SmartClassifierInput(
            StorageBusKind.Sata, new List<SmartAttributeSample>(), Nvme: null,
            VendorHealth: VendorHealthStatus.Warning, PredictsFailure: true);

        var result = SmartClassifier.Classify(input);

        var reason = Assert.Single(result.Reasons);
        Assert.Equal("smart.vendorHealth", reason.Code);
        Assert.Equal(ReasonSeverity.Act, reason.Severity);
        Assert.Equal(DriveStatus.Bad, result.Status);
    }

    [Fact]
    public void PredictsFailure_true_is_act_and_escalates_status_to_bad()
    {
        var input = new SmartClassifierInput(
            StorageBusKind.Sata, new List<SmartAttributeSample>(), Nvme: null,
            VendorHealth: null, PredictsFailure: true);

        var result = SmartClassifier.Classify(input);

        var reason = Assert.Single(result.Reasons);
        Assert.Equal("smart.vendorHealth", reason.Code);
        Assert.Equal(ReasonSeverity.Act, reason.Severity);
        Assert.Equal(DriveStatus.Bad, result.Status);
    }

    [Fact]
    public void Wear_95_percent_is_watch_and_caution()
    {
        var nvme = new NvmeHealthLog
        {
            CriticalWarning = 0,
            CompositeTemperatureKelvin = 300,
            AvailableSpare = 100,
            AvailableSpareThreshold = 10,
            PercentageUsed = 95,
            DataUnitsReadBytes = 0,
            DataUnitsWrittenBytes = 0,
            PowerCycles = 0,
            PowerOnHours = 0,
            UnsafeShutdowns = 0,
            MediaErrors = 0,
            ErrorInfoLogEntries = 0,
        };
        var input = new SmartClassifierInput(
            StorageBusKind.Nvme, new List<SmartAttributeSample>(), nvme, VendorHealth: null, PredictsFailure: null);

        var result = SmartClassifier.Classify(input);

        var reason = Assert.Single(result.Reasons);
        Assert.Equal("nvme.wearHigh", reason.Code);
        Assert.Equal(ReasonSeverity.Watch, reason.Severity);
        Assert.Equal(DriveStatus.Caution, result.Status);
    }

    [Fact]
    public void Wear_100_percent_is_act_not_watch()
    {
        var nvme = new NvmeHealthLog
        {
            CriticalWarning = 0,
            CompositeTemperatureKelvin = 300,
            AvailableSpare = 100,
            AvailableSpareThreshold = 10,
            PercentageUsed = 100,
            DataUnitsReadBytes = 0,
            DataUnitsWrittenBytes = 0,
            PowerCycles = 0,
            PowerOnHours = 0,
            UnsafeShutdowns = 0,
            MediaErrors = 0,
            ErrorInfoLogEntries = 0,
        };
        var input = new SmartClassifierInput(
            StorageBusKind.Nvme, new List<SmartAttributeSample>(), nvme, VendorHealth: null, PredictsFailure: null);

        var result = SmartClassifier.Classify(input);

        var reason = Assert.Single(result.Reasons);
        Assert.Equal("nvme.wearHigh", reason.Code);
        Assert.Equal(ReasonSeverity.Act, reason.Severity);
        Assert.Equal(DriveStatus.Warning, result.Status);
    }

    [Fact]
    public void Attribute_at_or_below_threshold_is_act_vendorHealth_and_flags_that_attribute_only()
    {
        var input = new SmartClassifierInput(
            StorageBusKind.Sata,
            new List<SmartAttributeSample>
            {
                new(9, "Power-On Hours", Current: 5, Worst: 5, Threshold: 10, Raw: 40000),
                HealthyAttr(194, "Temperature"),
            },
            Nvme: null,
            VendorHealth: null,
            PredictsFailure: null);

        var result = SmartClassifier.Classify(input);

        var reason = Assert.Single(result.Reasons);
        Assert.Equal("smart.vendorHealth", reason.Code);
        Assert.Equal(ReasonSeverity.Act, reason.Severity);
        Assert.Equal(DriveStatus.Warning, result.Status);
        Assert.Contains((byte)9, result.FlaggedAttributeIds);
        Assert.DoesNotContain((byte)194, result.FlaggedAttributeIds);
    }

    private static SmartClassification ClassifyCommandTimeout(ulong raw) =>
        SmartClassifier.Classify(new SmartClassifierInput(
            StorageBusKind.Sata,
            new List<SmartAttributeSample> { new(188, "Command Timeout", 100, 99, 0, Raw: raw) },
            Nvme: null,
            VendorHealth: null,
            PredictsFailure: null));

    [Fact]
    public void Command_timeout_packed_seagate_raw_decodes_to_largest_word()
    {
        var result = ClassifyCommandTimeout(4295032833);

        var reason = Assert.Single(result.Reasons);
        Assert.Equal("smart.commandTimeout", reason.Code);
        Assert.Equal("1 command timeout", reason.Summary);
        Assert.DoesNotContain("4295032833 command", reason.Summary);
    }

    [Fact]
    public void Command_timeout_plain_raw_is_unchanged()
    {
        var reason = Assert.Single(ClassifyCommandTimeout(3).Reasons);

        Assert.Equal("3 command timeouts", reason.Summary);
    }

    [Fact]
    public void Command_timeout_zero_raw_does_not_flag()
    {
        var result = ClassifyCommandTimeout(0);

        Assert.Empty(result.Reasons);
        Assert.Empty(result.FlaggedAttributeIds);
    }
}
