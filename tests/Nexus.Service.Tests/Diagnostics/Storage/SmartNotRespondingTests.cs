using Nexus.Service.Diagnostics.Storage;

namespace Nexus.Service.Tests.Diagnostics.Storage;

public class SmartNotRespondingTests
{
    [Fact]
    public void Placeholder_lists_the_drive_with_a_watch_reason_and_no_empty_reason_list()
    {
        var drive = SmartNotResponding.Placeholder("storage:S1", "ST4000DM004", "S1", "sata", 4_000_000_000_000);

        Assert.Equal("ST4000DM004", drive.Name);
        Assert.Equal("caution", drive.Status);
        Assert.Equal(new[] { "storage.notResponding" }, drive.StatusReasons);
        var reason = Assert.Single(drive.DetailedReasons);
        Assert.Equal("storage.notResponding", reason.Code);
        Assert.Equal(ReasonSeverity.Watch, reason.Severity);
        Assert.Equal("ST4000DM004 is not responding", reason.Summary);
        Assert.DoesNotContain("-", reason.Detail);
    }

    [Fact]
    public void A_good_last_entry_is_raised_to_caution_and_keeps_its_data()
    {
        var previous = new SmartDriveInfo { Id = "storage:S1", Name = "D", Status = "good", PowerOnHours = 1200, TemperatureC = 41 };

        var annotated = SmartNotResponding.Annotate(previous);

        Assert.Equal("caution", annotated.Status);
        Assert.Equal(1200UL, annotated.PowerOnHours);
        Assert.Null(annotated.TemperatureC);
        Assert.Contains("storage.notResponding", annotated.StatusReasons);
        Assert.Contains(annotated.DetailedReasons, r => r.Code == "storage.notResponding");
    }

    [Fact]
    public void A_worse_last_entry_keeps_its_status_and_existing_reasons()
    {
        var previous = new SmartDriveInfo
        {
            Name = "D",
            Status = "bad",
            StatusReasons = new[] { "reallocated" },
            DetailedReasons = new[] { new SmartReason("reallocated", ReasonSeverity.Act, "s", "d") },
        };

        var annotated = SmartNotResponding.Annotate(previous);

        Assert.Equal("bad", annotated.Status);
        Assert.Equal(2, annotated.DetailedReasons.Count);
    }

    [Fact]
    public void Annotating_twice_adds_the_reason_once()
    {
        var once = SmartNotResponding.Annotate(new SmartDriveInfo { Name = "D", Status = "good" });

        Assert.Same(once, SmartNotResponding.Annotate(once));
    }
}
