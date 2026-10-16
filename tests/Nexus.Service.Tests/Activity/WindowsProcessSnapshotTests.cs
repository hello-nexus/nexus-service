using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;
using Nexus.Service.Activity.Native;
using Xunit;

namespace Nexus.Service.Tests.Activity;

public class WindowsProcessSnapshotTests
{
    private const int EntrySize = 256;

    [Fact]
    public void Parse_ReadsFieldsFromTheKernelLayout_AndSkipsTheIdleEntry()
    {
        var buffer = new byte[EntrySize * 3 + 256];
        var handle = GCHandle.Alloc(buffer, GCHandleType.Pinned);
        try
        {
            var baseAddress = handle.AddrOfPinnedObject().ToInt64();
            WriteEntry(buffer, baseAddress, 0, nextOffset: EntrySize, pid: 0, name: "", workingSet: 0, cpu: (0, 0), create: 0, io: (0, 0));
            WriteEntry(buffer, baseAddress, EntrySize, nextOffset: EntrySize, pid: 1234, name: "Game.EXE", workingSet: 5_000_000, cpu: (30, 12), create: 133_000_000_000_000_000, io: (111, 222));
            WriteEntry(buffer, baseAddress, EntrySize * 2, nextOffset: 0, pid: 4, name: "System", workingSet: 40, cpu: (1, 2), create: 0, io: (0, 0));

            var entries = new List<ProcessSnapshotEntry>();
            WindowsProcessSnapshot.Parse(buffer, entries);

            Assert.Equal(2, entries.Count);
            Assert.Equal(new ProcessSnapshotEntry(1234, "Game", 5_000_000, 42, 133_000_000_000_000_000, 111, 222), entries[0]);
            Assert.Equal("System", entries[1].Name);
            Assert.Equal(4, entries[1].Pid);
        }
        finally
        {
            handle.Free();
        }
    }

    [Fact]
    public void Parse_NamePointerOutsideTheBuffer_YieldsEmptyName()
    {
        var buffer = new byte[EntrySize * 3 + 256];
        var handle = GCHandle.Alloc(buffer, GCHandleType.Pinned);
        try
        {
            WriteEntry(buffer, handle.AddrOfPinnedObject().ToInt64(), 0, nextOffset: 0, pid: 7, name: "x", workingSet: 1, cpu: (0, 0), create: 0, io: (0, 0));
            BitConverter.TryWriteBytes(buffer.AsSpan(64), 0x10L);
            var entries = new List<ProcessSnapshotEntry>();
            WindowsProcessSnapshot.Parse(buffer, entries);
            Assert.Equal("", Assert.Single(entries).Name);
        }
        finally
        {
            handle.Free();
        }
    }

    [Fact]
    public void Parse_ChainRunningPastTheBuffer_StopsAtTheLastCompleteEntry()
    {
        var buffer = new byte[EntrySize * 3 + 256];
        var handle = GCHandle.Alloc(buffer, GCHandleType.Pinned);
        try
        {
            WriteEntry(buffer, handle.AddrOfPinnedObject().ToInt64(), 0, nextOffset: EntrySize * 10, pid: 7, name: "a", workingSet: 1, cpu: (0, 0), create: 0, io: (0, 0));
            var entries = new List<ProcessSnapshotEntry>();
            WindowsProcessSnapshot.Parse(buffer, entries);
            Assert.Equal(7, Assert.Single(entries).Pid);
        }
        finally
        {
            handle.Free();
        }
    }

    private static void WriteEntry(byte[] buffer, long baseAddress, int offset, int nextOffset, int pid, string name, long workingSet, (long user, long kernel) cpu, long create, (long read, long write) io)
    {
        var span = buffer.AsSpan(offset);
        BitConverter.TryWriteBytes(span, (uint)nextOffset);
        BitConverter.TryWriteBytes(span[32..], create);
        BitConverter.TryWriteBytes(span[40..], cpu.user);
        BitConverter.TryWriteBytes(span[48..], cpu.kernel);
        var nameBytes = Encoding.Unicode.GetBytes(name);
        BitConverter.TryWriteBytes(span[56..], (ushort)nameBytes.Length);
        // Name text lives in the trailing slack of the buffer, like the kernel's own string area.
        var nameOffset = EntrySize * 3 + (offset / EntrySize) * 64;
        nameBytes.CopyTo(buffer, nameOffset);
        BitConverter.TryWriteBytes(span[64..], baseAddress + nameOffset);
        BitConverter.TryWriteBytes(span[80..], (long)pid);
        BitConverter.TryWriteBytes(span[144..], workingSet);
        BitConverter.TryWriteBytes(span[232..], io.read);
        BitConverter.TryWriteBytes(span[240..], io.write);
    }
}
