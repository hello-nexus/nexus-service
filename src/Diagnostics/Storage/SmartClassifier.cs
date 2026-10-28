using System.Collections.Generic;
using System.Linq;

namespace Nexus.Service.Diagnostics.Storage;

/// <summary>Bus family a drive is attached through. Mirrors the /diagnostics/smart wire "bus" values.</summary>
public enum StorageBusKind
{
    Nvme,
    Sata,
    Usb,
    Raid,
    Other,
}

/// <summary>Mirrors DiskInfoToolkit.Smart.StorageHealthStatus without a package dependency, so the classifier stays pure.</summary>
public enum VendorHealthStatus
{
    Good,
    Caution,
    Warning,
    Bad,
}

public enum ReasonSeverity
{
    Watch,
    Act,
}

public enum DriveStatus
{
    Good,
    Caution,
    Warning,
    Bad,
    Unknown,
}

/// <summary>One SMART attribute row, mirroring DiskInfoToolkit.Smart.SmartAttributeEntry's field shapes.</summary>
public sealed record SmartAttributeSample(byte Id, string Name, byte Current, byte Worst, byte Threshold, ulong Raw);

/// <summary>One classification finding. Code is the stable machine key (web maps to i18n); Summary/Detail are plain English, supplemental only.</summary>
public sealed record SmartReason(string Code, ReasonSeverity Severity, string Summary, string Detail);

public sealed record SmartClassifierInput(
    StorageBusKind Bus,
    IReadOnlyList<SmartAttributeSample> Attributes,
    NvmeHealthLog? Nvme,
    VendorHealthStatus? VendorHealth,
    bool? PredictsFailure);

public sealed record SmartClassification(DriveStatus Status, IReadOnlyList<SmartReason> Reasons, IReadOnlySet<byte> FlaggedAttributeIds);

/// <summary>
/// Pure SMART/NVMe health classification. No LibreHardwareMonitor or
/// DiskInfoToolkit dependency - SmartHealthMonitor maps those types into
/// SmartClassifierInput before calling Classify.
/// </summary>
public static class SmartClassifier
{
    // Backblaze critical five ATA attributes: id -> (reason code, pluralized noun for the raw count).
    private static readonly Dictionary<byte, (string Code, string Noun)> BackblazeAttributes = new()
    {
        [5] = ("smart.reallocated", "reallocated sector"),
        [187] = ("smart.uncorrectable", "reported uncorrectable error"),
        [188] = ("smart.commandTimeout", "command timeout"),
        [197] = ("smart.pendingSectors", "pending sector"),
        [198] = ("smart.offlineUncorrectable", "offline uncorrectable sector"),
    };

    // Ids whose raw count escalates watch -> act above 100 (5, 197, 198 per the Backblaze study).
    private static readonly HashSet<byte> EscalatingIds = new() { 5, 197, 198 };

    // Seagate packs attribute 188 as three 16-bit counters; the largest word never undercounts any of them.
    private static ulong DecodeCount(SmartAttributeSample att)
    {
        if (att.Id != 188 || att.Raw <= 0xFFFF) return att.Raw;
        return Math.Max(att.Raw & 0xFFFF, Math.Max((att.Raw >> 16) & 0xFFFF, (att.Raw >> 32) & 0xFFFF));
    }

    public static SmartClassification Classify(SmartClassifierInput input)
    {
        var reasons = new List<SmartReason>();
        var flagged = new HashSet<byte>();
        var vendorHealthAdded = false;
        var hasCriticalWarning = false;

        // ATA rules only apply to attribute ids that are an ATA/SATA SMART convention;
        // an NVMe drive's SmartAttributes list should be empty, but guard explicitly.
        if (input.Bus != StorageBusKind.Nvme)
        {
            foreach (var att in input.Attributes)
            {
                if (!BackblazeAttributes.TryGetValue(att.Id, out var meta)) continue;
                var count = DecodeCount(att);
                if (count == 0) continue;

                var severity = EscalatingIds.Contains(att.Id) && count > 100 ? ReasonSeverity.Act : ReasonSeverity.Watch;
                var plural = count == 1 ? meta.Noun : meta.Noun + "s";
                reasons.Add(new SmartReason(meta.Code, severity,
                    $"{count} {plural}",
                    $"SMART attribute {att.Id:D2} ({att.Name}) raw value is {att.Raw}, counted as {count}."));
                flagged.Add(att.Id);
            }
        }

        // Vendor-computed threshold breach applies to any attribute on any bus.
        foreach (var att in input.Attributes)
        {
            if (att.Threshold == 0) continue;
            if (att.Current > att.Threshold) continue;

            reasons.Add(new SmartReason("smart.vendorHealth", ReasonSeverity.Act,
                $"{att.Name} at or below its vendor threshold",
                $"Attribute {att.Id} ({att.Name}) current value {att.Current} is at or below its threshold {att.Threshold}."));
            flagged.Add(att.Id);
            vendorHealthAdded = true;
        }

        if (input.PredictsFailure == true)
        {
            reasons.Add(new SmartReason("smart.vendorHealth", ReasonSeverity.Act,
                "Drive firmware predicts imminent failure",
                "The drive's SMART PredictsFailure flag is set."));
            vendorHealthAdded = true;
        }

        if (input.Nvme is { } nvme)
        {
            if (nvme.CriticalWarning != 0)
            {
                reasons.Add(new SmartReason("nvme.criticalWarning", ReasonSeverity.Act,
                    "NVMe controller reports a critical warning",
                    DescribeCriticalWarning(nvme.CriticalWarning)));
                hasCriticalWarning = true;
            }

            if (nvme.AvailableSpare < nvme.AvailableSpareThreshold)
            {
                reasons.Add(new SmartReason("nvme.spareLow", ReasonSeverity.Act,
                    "Available spare capacity is below the drive's threshold",
                    $"Available spare {nvme.AvailableSpare}% is below threshold {nvme.AvailableSpareThreshold}%."));
            }

            if (nvme.PercentageUsed >= 100)
            {
                reasons.Add(new SmartReason("nvme.wearHigh", ReasonSeverity.Act,
                    "Drive has reached its rated endurance",
                    $"NVMe percentage used is {nvme.PercentageUsed}%."));
            }
            else if (nvme.PercentageUsed >= 90)
            {
                reasons.Add(new SmartReason("nvme.wearHigh", ReasonSeverity.Watch,
                    "Drive is approaching its rated endurance",
                    $"NVMe percentage used is {nvme.PercentageUsed}%."));
            }

            if (nvme.MediaErrors > 0)
            {
                reasons.Add(new SmartReason("nvme.mediaErrors", ReasonSeverity.Watch,
                    $"{nvme.MediaErrors} media error(s) reported",
                    $"NVMe media and data integrity error count is {nvme.MediaErrors}."));
            }
        }

        // DIT's own summarized health status only contributes a reason when nothing
        // else already flagged smart.vendorHealth, so a single vendor-health problem
        // does not appear twice.
        if (!vendorHealthAdded && input.VendorHealth is { } vh && vh != VendorHealthStatus.Good)
        {
            var severity = vh == VendorHealthStatus.Caution ? ReasonSeverity.Watch : ReasonSeverity.Act;
            reasons.Add(new SmartReason("smart.vendorHealth", severity,
                "Vendor SMART health summary reports a problem",
                $"Vendor-summarized health status is {vh}."));
        }

        var hasAct = reasons.Any(r => r.Severity == ReasonSeverity.Act);
        var hasWatch = reasons.Any(r => r.Severity == ReasonSeverity.Watch);
        var status = hasAct
            ? (input.PredictsFailure == true || hasCriticalWarning ? DriveStatus.Bad : DriveStatus.Warning)
            : hasWatch
                ? DriveStatus.Caution
                : DriveStatus.Good;

        return new SmartClassification(status, reasons, flagged);
    }

    private static string DescribeCriticalWarning(byte flags)
    {
        var bits = new List<string>();
        if ((flags & 0x01) != 0) bits.Add("available spare below threshold");
        if ((flags & 0x02) != 0) bits.Add("temperature exceeded a threshold");
        if ((flags & 0x04) != 0) bits.Add("NVM subsystem reliability degraded");
        if ((flags & 0x08) != 0) bits.Add("media placed in read-only mode");
        if ((flags & 0x10) != 0) bits.Add("volatile memory backup device failed");
        return bits.Count > 0
            ? $"NVMe critical warning bits set: {string.Join(", ", bits)}."
            : "NVMe critical warning byte is nonzero.";
    }
}
