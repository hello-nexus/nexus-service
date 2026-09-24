using System.IO;
using Nexus.Service.Platform;
using Xunit;

namespace Nexus.Service.Tests;

/// <summary>
/// ServiceLog tees Console.Out / Console.Error to a rotating nexus-service.log
/// under per-platform LocalAppData. We only smoke-test that it doesn't throw
/// and that subsequent Console.WriteLine reaches the resolved file. Full
/// rotation behaviour is hard to assert deterministically without a 5 MB
/// write so we cover that path indirectly via the helper.
/// </summary>
public class ServiceLogTests
{
    [NonWindowsFact]
    public void Console_write_after_init_appears_in_log_file()
    {
        // The tee is process-global; leaving it installed makes every later
        // Console write in the suite a locked, flushed file write.
        try
        {
            ServiceLog.Initialize();
            var marker = "service-log-marker-" + Guid.NewGuid().ToString("N");
            Console.WriteLine(marker);

            var path = ServiceLog.LogFilePath;
            Assert.NotNull(path);
            // FileShare.Read keeps the writer open; reopening for read must
            // succeed concurrently.
            var contents = File.ReadAllText(path!);
            Assert.Contains(marker, contents);
        }
        finally
        {
            ServiceLog.ResetForTests();
        }
    }

    // Records whether the writing thread already holds Console.Out's monitor
    // when a line reaches the original console writer.
    private sealed class LockOrderProbe : StringWriter
    {
        public bool? HeldConsoleOutOnEntry;

        public override void WriteLine(string? value)
        {
            HeldConsoleOutOnEntry = Monitor.IsEntered(Console.Out);
            base.WriteLine(value);
        }
    }

    // Unix ConsolePal locks Console.Out inside every console write. A
    // Console.Out.WriteLine holds that lock before it reaches the original
    // writer; the ServiceLog mirror must too, or the two paths take the same
    // two locks in opposite orders and deadlock on the first interleaving.
    [NonWindowsFact]
    public void ServiceLog_mirror_holds_ConsoleOut_before_the_original_writer()
    {
        var realOut = Console.Out;
        var probe = new LockOrderProbe();
        try
        {
            Console.SetOut(probe);
            ServiceLog.Initialize();

            Console.Out.WriteLine("via console");
            Assert.True(probe.HeldConsoleOutOnEntry);

            probe.HeldConsoleOutOnEntry = null;
            ServiceLog.Info("via service-log");
            Assert.True(probe.HeldConsoleOutOnEntry);
        }
        finally
        {
            ServiceLog.ResetForTests();
            Console.SetOut(realOut);
        }
    }

    [NonWindowsFact]
    public void Initialize_is_idempotent()
    {
        try
        {
            ServiceLog.Initialize();
            var first = ServiceLog.LogFilePath;
            var teeAfterFirst = Console.Out;

            ServiceLog.Initialize();

            Assert.Equal(first, ServiceLog.LogFilePath);
            // A second call must not wrap the existing tee in another one.
            Assert.Same(teeAfterFirst, Console.Out);
        }
        finally
        {
            ServiceLog.ResetForTests();
        }
    }
}
