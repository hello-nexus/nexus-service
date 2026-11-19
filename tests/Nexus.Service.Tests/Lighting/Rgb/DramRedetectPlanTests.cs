using System.Collections.Generic;
using Nexus.Service.Lighting.Rgb;
using Xunit;

namespace Nexus.Service.Tests.Lighting.Rgb;

public class DramRedetectPlanTests
{
    private const string Dimm1 = "openrgb-l-I2C__PawnIO_SMBus_i801_0__address_0x71";
    private const string Dimm2 = "openrgb-l-I2C__PawnIO_SMBus_i801_0__address_0x73";

    [Fact]
    public void KnownAndAbsentSchedulesFirstPass()
    {
        var missing = DramRedetectPlan.MissingKnown(new[] { Dimm1, Dimm2 }, new HashSet<string>(), new HashSet<string>());

        Assert.Equal(2, missing.Count);
        Assert.Equal(DramRedetectPlan.Delays[0], DramRedetectPlan.NextDelay(0, missing.Count));
    }

    [Fact]
    public void NothingKnownSchedulesNothing()
    {
        var missing = DramRedetectPlan.MissingKnown(
            new[] { "openrgb-l-HID_foo", "openrgb-l-I2C__NVAPI_GPU_0", "keeb" },
            new HashSet<string>(), new HashSet<string>());

        Assert.Empty(missing);
        Assert.Null(DramRedetectPlan.NextDelay(0, missing.Count));
    }

    [Fact]
    public void AppearingStopsTheSeries()
    {
        var missing = DramRedetectPlan.MissingKnown(new[] { Dimm1, Dimm2 }, new HashSet<string> { Dimm1, Dimm2 }, new HashSet<string>());

        Assert.Empty(missing);
        Assert.Null(DramRedetectPlan.NextDelay(1, missing.Count));
    }

    [Fact]
    public void PartialPresenceStillCountsTheMissingOne()
    {
        var missing = DramRedetectPlan.MissingKnown(new[] { Dimm1, Dimm2 }, new HashSet<string> { Dimm1 }, new HashSet<string>());

        Assert.Equal(new[] { Dimm2 }, missing);
    }

    [Fact]
    public void PassesAreBounded()
    {
        for (var attempt = 0; attempt < DramRedetectPlan.Delays.Length; attempt++)
        {
            Assert.NotNull(DramRedetectPlan.NextDelay(attempt, 1));
        }
        Assert.Null(DramRedetectPlan.NextDelay(DramRedetectPlan.Delays.Length, 1));
    }

    [Fact]
    public void DuplicateKnownIdsCountOnce()
    {
        Assert.Single(DramRedetectPlan.MissingKnown(new[] { Dimm1, Dimm1 }, new HashSet<string>(), new HashSet<string>()));
    }

    [Theory]
    [InlineData("openrgb-l-I2C__PawnIO_SMBus_i801_0__address_0x40-3", "openrgb-l-I2C__PawnIO_SMBus_i801_0__address_0x40")]
    [InlineData("openrgb-l-I2C__PawnIO_SMBus_i801_0__address_0x40:z2", "openrgb-l-I2C__PawnIO_SMBus_i801_0__address_0x40")]
    [InlineData("openrgb-l-I2C__PawnIO_SMBus_i801_0__address_0x71", "openrgb-l-I2C__PawnIO_SMBus_i801_0__address_0x71")]
    public void Card_ids_normalize_to_device_ids(string card, string device)
        => Assert.Equal(device, DramRedetectPlan.DeviceIdOf(card));

    [Fact]
    public void Seeding_keeps_one_normalized_smbus_id_per_device()
    {
        var seeded = DramRedetectPlan.SeedFromCardIds(new[] { Dimm1, Dimm1 + "-1", "openrgb-l-HID_x", "openrgb-l-I2C__NVAPI_0" });

        Assert.Equal(new[] { Dimm1 }, seeded);
    }

    [Fact]
    public void Excluded_ids_are_not_missing()
    {
        Assert.Empty(DramRedetectPlan.MissingKnown(new[] { Dimm1 }, new HashSet<string>(), new HashSet<string> { Dimm1 }));
    }

    [Fact]
    public void A_known_id_that_shows_up_as_another_device_type_is_forgotten()
    {
        var next = DramRedetectPlan.Reconcile(new Dictionary<string, int> { [Dimm1] = 1, [Dimm2] = 2 }, new[] { Dimm2 }, new[] { Dimm1 });

        Assert.Equal(new Dictionary<string, int> { [Dimm2] = 0 }, next);
    }

    [Fact]
    public void A_device_missing_for_the_cap_of_sessions_is_forgotten()
    {
        var known = new Dictionary<string, int> { [Dimm1] = 0 };
        for (var session = 1; session < DramRedetectPlan.MaxMissedSessions; session++)
        {
            known = DramRedetectPlan.RecordMissedSession(known, new[] { Dimm1 });
            Assert.Equal(session, known[Dimm1]);
        }

        Assert.Empty(DramRedetectPlan.RecordMissedSession(known, new[] { Dimm1 }));
    }
}
