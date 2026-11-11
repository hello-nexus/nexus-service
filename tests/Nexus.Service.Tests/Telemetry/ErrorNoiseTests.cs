using System.IO;
using Nexus.Service.Telemetry;
using Xunit;

namespace Nexus.Service.Tests.Telemetry;

public class ErrorNoiseTests
{
    private const string Kestrel = "Microsoft.AspNetCore.Server.Kestrel";

    [Fact]
    public void Serial_event_loop_teardown_is_library_noise()
    {
        var stack = "   at Interop.Kernel32.GetOverlappedResult(SafeFileHandle, NativeOverlapped*, Int32&, Boolean) + 0x4f\n" +
                    "   at System.IO.Ports.SerialStream.EventLoopRunner.WaitForCommEvent() + 0x1c5";
        Assert.True(ErrorNoise.IsLibraryTeardown(new ObjectDisposedException("SafeFileHandle"), stack));
    }

    [Fact]
    public void Bundled_hardware_monitor_memory_retry_fault_is_library_noise()
    {
        var stack = "   at LibreHardwareMonitor.Hardware.Memory.MemoryGroup.<>c__DisplayClass17_0.<<StartRetryTask>b__0>d.MoveNext() + 0x92";
        Assert.True(ErrorNoise.IsLibraryTeardown(new NullReferenceException(), stack));
    }

    [Fact]
    public void The_same_exception_types_from_nexus_code_are_still_reported()
    {
        var stack = "   at Nexus.Service.Lighting.Rgb.OpenRgbProcessManager.<Start>b__0() + 0xf";
        Assert.False(ErrorNoise.IsLibraryTeardown(new ObjectDisposedException("Process"), stack));
        Assert.False(ErrorNoise.IsLibraryTeardown(new NullReferenceException(), stack));
        Assert.False(ErrorNoise.IsLibraryTeardown(new ObjectDisposedException("Process"), null));
    }

    [Fact]
    public void An_unobserved_task_with_one_foreign_inner_is_reported()
    {
        var mixed = new AggregateException(new ObjectDisposedException("x"), new IOException("y"));
        Assert.False(ErrorNoise.IsUnobservedLibraryTeardown(mixed));
    }

    [Fact]
    public void Kestrel_download_cut_short_by_the_client_is_noise()
    {
        var ex = new InvalidOperationException("Response Content-Length mismatch: too few bytes written (6946816 of 10338388).");
        Assert.True(ErrorNoise.IsKestrelClientDisconnect(Kestrel, ex, clientAborted: true));
        Assert.True(ErrorNoise.IsKestrelClientDisconnect(Kestrel + ".Http2", ex, clientAborted: true));
    }

    [Fact]
    public void Kestrel_tls_write_to_a_closed_peer_is_noise()
    {
        Assert.True(ErrorNoise.IsKestrelClientDisconnect(Kestrel, new IOException("The encryption operation failed, see inner exception."), clientAborted: false));
    }

    [Fact]
    public void Kestrel_out_of_socket_buffer_space_is_noise_directly_or_wrapped()
    {
        var sock = new System.Net.Sockets.SocketException(10055);
        Assert.True(ErrorNoise.IsKestrelClientDisconnect(Kestrel, sock, clientAborted: false));
        Assert.True(ErrorNoise.IsKestrelClientDisconnect(Kestrel, new IOException("write failed", sock), clientAborted: false));
        Assert.False(ErrorNoise.IsKestrelClientDisconnect(Kestrel, sock, clientAborted: false, Microsoft.Extensions.Logging.LogLevel.Critical));
        Assert.False(ErrorNoise.IsKestrelClientDisconnect(Kestrel, new System.Net.Sockets.SocketException(10054), clientAborted: false));
        Assert.False(ErrorNoise.IsKestrelClientDisconnect("Nexus.Service.Routes", sock, clientAborted: false));
    }

    [Fact]
    public void Other_kestrel_errors_and_other_categories_are_reported()
    {
        Assert.False(ErrorNoise.IsKestrelClientDisconnect(Kestrel, new InvalidOperationException("Response Content-Length mismatch: too many bytes written (5 of 3)."), true));
        Assert.False(ErrorNoise.IsKestrelClientDisconnect(Kestrel, new IOException("disk full"), true));
        Assert.False(ErrorNoise.IsKestrelClientDisconnect("Nexus.Service.Routes", new InvalidOperationException("Response Content-Length mismatch: too few bytes written (1 of 2)."), true));
    }

    [Fact]
    public void A_cut_short_response_the_client_did_not_abort_is_a_handler_fault_and_is_reported()
    {
        var ex = new InvalidOperationException("Response Content-Length mismatch: too few bytes written (1 of 2).");
        Assert.False(ErrorNoise.IsKestrelClientDisconnect(Kestrel, ex, clientAborted: false));
    }

    [Fact]
    public void The_abort_ledger_answers_once_per_request_and_ignores_unknown_ids()
    {
        ClientAbortLedger.Note("conn-1:7");

        Assert.False(ClientAbortLedger.Take("conn-1:8"));
        Assert.False(ClientAbortLedger.Take(null));
        Assert.True(ClientAbortLedger.Take("conn-1:7"));
        Assert.False(ClientAbortLedger.Take("conn-1:7"));
    }
}
