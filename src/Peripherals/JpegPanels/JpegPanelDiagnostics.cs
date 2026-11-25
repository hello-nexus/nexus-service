namespace Nexus.Service.Peripherals.JpegPanels;

internal static class JpegPanelDiagnostics
{
    /// <summary>Win32 code from the last HID write; 0 means none recorded, -1 the handle was closing, -2 zero bytes transferred.</summary>
    internal static string DescribeWriteError(int code) => code switch
    {
        0 => "no OS error recorded",
        -1 => "handle closing",
        -2 => "write completed with zero bytes",
        5 => "Win32 error 5 (access denied)",
        6 => "Win32 error 6 (invalid handle)",
        31 => "Win32 error 31 (device not functioning)",
        121 => "Win32 error 121 (semaphore timeout)",
        995 => "Win32 error 995 (I/O aborted)",
        1167 => "Win32 error 1167 (device not connected)",
        1460 => "Win32 error 1460 (write did not complete in time)",
        _ => $"Win32 error {code}",
    };

    internal static string FormatWriteFailure(
        int chunkIndex, int chunkBytes, int offset, int total, long writeMs, int error)
    {
        return $"report write rejected at chunk {chunkIndex} ({chunkBytes} byte chunk, {offset} of {total} bytes sent), "
            + $"write took {writeMs} ms, {DescribeWriteError(error)}";
    }
}
