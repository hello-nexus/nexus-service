using Nexus.Service.Diagnostics;
using Xunit;

namespace Nexus.Service.Tests.Diagnostics;

public class FastStartupTests
{
    [Theory]
    [InlineData(1, null, 1, null, true, false, true)]
    [InlineData(0, null, 1, null, false, false, true)]
    [InlineData(1, null, null, 1, true, false, true)]
    [InlineData(1, null, 0, 1, false, false, false)]
    [InlineData(1, null, null, null, false, false, false)]
    [InlineData(0, 1, 1, null, true, true, true)]
    [InlineData(1, 0, 1, null, false, true, true)]
    [InlineData(1, 1, 0, null, false, true, false)]
    public void State_is_derived(int? hiberboot, int? policy, int? hibernate, int? hibernateDefault,
        bool enabled, bool policyManaged, bool hibernateAvailable)
    {
        var s = FastStartupState.Derive(hiberboot, policy, hibernate, hibernateDefault);
        Assert.True(s.Supported);
        Assert.Equal(enabled, s.Enabled);
        Assert.Equal(policyManaged, s.PolicyManaged);
        Assert.Equal(hibernateAvailable, s.HibernateAvailable);
    }

    [Fact]
    public void Unsupported_reports_everything_false()
    {
        Assert.Equal(new FastStartupState(false, false, false, false), FastStartupState.Unsupported);
    }
}
