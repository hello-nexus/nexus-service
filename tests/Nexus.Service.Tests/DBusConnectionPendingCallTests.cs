using System;
using System.IO;
using System.Threading.Tasks;
using Nexus.Service.Platform.Linux.DBus;
using Xunit;

namespace Nexus.Service.Tests;

public class DBusConnectionPendingCallTests
{
    [Fact]
    public async Task A_call_that_cannot_be_sent_leaves_no_waiter_registered()
    {
        // Never started, so SendMessage throws "not connected". A waiter left in the table would be faulted with
        // nobody awaiting it by the next disconnect.
        using var bus = new DBusConnection(DBusBusKind.Session);

        await Assert.ThrowsAsync<IOException>(() => bus.AddMatchAsync("type='signal'"));

        Assert.Equal(0, bus.PendingCallCount);
    }
}
