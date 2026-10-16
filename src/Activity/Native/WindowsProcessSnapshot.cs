using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace Nexus.Service.Activity.Native;

/// <summary>One process as the kernel reports it in a single NtQuerySystemInformation call; no process handle is opened to read any field.</summary>
internal readonly record struct ProcessSnapshotEntry(
    int Pid,
    string Name,
    long WorkingSetBytes,
    long CpuTime100ns,
    long CreateFileTime,
    long ReadTransferBytes,
    long WriteTransferBytes);

internal static class WindowsProcessSnapshot
{
    private const int SystemProcessInformation = 5;
    private const int StatusInfoLengthMismatch = unchecked((int)0xC0000004);

    // SYSTEM_PROCESS_INFORMATION field offsets (64-bit layouts).
    private const int OffsetNext = 0;
    private const int OffsetCreateTime = 32;
    private const int OffsetUserTime = 40;
    private const int OffsetKernelTime = 48;
    private const int OffsetImageNameLength = 56;
    private const int OffsetImageNameBuffer = 64;
    private const int OffsetPid = 80;
    private const int OffsetWorkingSet = 144;
    private const int OffsetReadTransfer = 232;
    private const int OffsetWriteTransfer = 240;
    private const int MinEntrySize = 248;

    private static readonly object s_gate = new();
    private static byte[] s_buffer = new byte[1 << 20];

    /// <summary>Replaces <paramref name="into"/> with every process except the idle pid; false when the kernel call fails.</summary>
    public static bool TryRead(List<ProcessSnapshotEntry> into)
    {
        lock (s_gate)
        {
            for (var attempt = 0; attempt < 4; attempt++)
            {
                int status;
                uint needed;
                unsafe
                {
                    fixed (byte* p = s_buffer)
                    {
                        status = NtQuerySystemInformation(SystemProcessInformation, p, (uint)s_buffer.Length, out needed);
                    }
                }
                if (status >= 0)
                {
                    Parse(s_buffer, into);
                    return true;
                }
                if (status != StatusInfoLengthMismatch)
                {
                    return false;
                }
                s_buffer = new byte[Math.Max(s_buffer.Length * 2, (int)needed + (1 << 16))];
            }
            return false;
        }
    }

    /// <summary>Walks the SYSTEM_PROCESS_INFORMATION chain. The image name pointers inside <paramref name="buffer"/> are absolute, so the array must be the one the kernel filled, at its current address.</summary>
    internal static unsafe void Parse(byte[] buffer, List<ProcessSnapshotEntry> into)
    {
        into.Clear();
        fixed (byte* basePtr = buffer)
        {
            long offset = 0;
            while (offset + MinEntrySize <= buffer.Length)
            {
                var e = basePtr + offset;
                var pid = (int)*(long*)(e + OffsetPid);
                if (pid != 0)
                {
                    var nameBytes = *(ushort*)(e + OffsetImageNameLength);
                    var namePtr = *(byte**)(e + OffsetImageNameBuffer);
                    var name = namePtr >= basePtr && namePtr + nameBytes <= basePtr + buffer.Length
                        ? new string((char*)namePtr, 0, nameBytes / 2)
                        : "";
                    if (name.Length > 4 && name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                    {
                        name = name[..^4];
                    }
                    into.Add(new ProcessSnapshotEntry(
                        pid,
                        name,
                        *(long*)(e + OffsetWorkingSet),
                        *(long*)(e + OffsetUserTime) + *(long*)(e + OffsetKernelTime),
                        *(long*)(e + OffsetCreateTime),
                        *(long*)(e + OffsetReadTransfer),
                        *(long*)(e + OffsetWriteTransfer)));
                }
                var next = *(uint*)(e + OffsetNext);
                if (next == 0)
                {
                    break;
                }
                offset += next;
            }
        }
    }

    [DllImport("ntdll.dll")]
    private static extern unsafe int NtQuerySystemInformation(int infoClass, byte* info, uint length, out uint returnLength);
}
