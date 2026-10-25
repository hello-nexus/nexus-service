using System.Text.Json;
using Nexus.Service.Lighting.Scene;

namespace Nexus.Service.Tests.Lighting.Scene;

public sealed class PcSetupCaseLayoutCapTests
{
    private static PcSetupError? Validate(int rawBytes)
    {
        // A JSON string's raw text is its length plus two quotes.
        var layout = JsonSerializer.SerializeToElement(new string('x', rawBytes - 2));
        var setup = new PcSetup { Resolved = new PcSetupResolved { CaseLayout = layout } };
        return PcSetupValidation.Validate(setup);
    }

    [Fact]
    public void A_40_KB_case_layout_is_accepted()
    {
        Assert.Null(Validate(40 * 1000));
    }

    [Fact]
    public void A_case_layout_at_64_KiB_is_accepted_and_one_byte_over_is_rejected()
    {
        Assert.Null(Validate(64 * 1024));
        Assert.Equal("value_too_large", Validate(64 * 1024 + 1)?.Code);
    }
}
