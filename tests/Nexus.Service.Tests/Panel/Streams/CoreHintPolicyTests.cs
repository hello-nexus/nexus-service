using System;
using System.Collections.Generic;
using Nexus.Service.Platform.Windows;
using Xunit;

namespace Nexus.Service.Tests.Panel.Streams;

public sealed class CoreHintPolicyTests
{
    private static byte[] Record(uint id, ushort group, byte index, byte efficiency, uint type = 0, uint size = 32)
    {
        var record = new byte[size];
        BitConverter.GetBytes(size).CopyTo(record, 0);
        BitConverter.GetBytes(type).CopyTo(record, 4);
        BitConverter.GetBytes(id).CopyTo(record, 8);
        BitConverter.GetBytes(group).CopyTo(record, 12);
        record[14] = index;
        record[18] = efficiency;
        return record;
    }

    private static byte[] Concat(params byte[][] parts)
    {
        var all = new List<byte>();
        foreach (var part in parts) all.AddRange(part);
        return all.ToArray();
    }

    [Fact]
    public void Parses_cpu_set_records_and_skips_other_types()
    {
        var buffer = Concat(Record(0x100, 0, 0, 1), Record(0x101, 0, 1, 1, type: 7), Record(0x110, 0, 16, 0));
        var sets = CoreHintPolicy.ParseCpuSets(buffer);
        Assert.Equal(2, sets.Count);
        Assert.Equal(0x100u, sets[0].Id);
        Assert.Equal((byte)16, sets[1].LogicalProcessorIndex);
        Assert.Equal((byte)0, sets[1].EfficiencyClass);
    }

    [Fact]
    public void A_truncated_or_zero_sized_record_ends_the_parse()
    {
        var good = Record(0x100, 0, 0, 1);
        Assert.Single(CoreHintPolicy.ParseCpuSets(Concat(good, new byte[8])));
        Assert.Empty(CoreHintPolicy.ParseCpuSets(Record(1, 0, 0, 1, size: 20).AsSpan(0, 12)));
        var zero = Record(1, 0, 0, 1);
        BitConverter.GetBytes(0u).CopyTo(zero, 0);
        Assert.Empty(CoreHintPolicy.ParseCpuSets(zero));
    }

    [Fact]
    public void Selects_the_highest_efficiency_class_on_a_hybrid_cpu()
    {
        var sets = CoreHintPolicy.ParseCpuSets(Concat(
            Record(1, 0, 0, 1), Record(2, 0, 1, 1), Record(3, 0, 16, 0), Record(4, 0, 17, 0)));
        var best = CoreHintPolicy.SelectBestClass(sets);
        Assert.Equal(new uint[] { 1, 2 }, best.ConvertAll(s => s.Id));
    }

    [Fact]
    public void Selects_nothing_when_every_core_shares_a_class()
    {
        var sets = CoreHintPolicy.ParseCpuSets(Concat(Record(1, 0, 0, 0), Record(2, 0, 1, 0)));
        Assert.Empty(CoreHintPolicy.SelectBestClass(sets));
        Assert.Empty(CoreHintPolicy.SelectBestClass(Array.Empty<CoreHintPolicy.CpuSet>()));
    }

    [Theory]
    [InlineData(null, CoreHintMode.Soft)]
    [InlineData("", CoreHintMode.Soft)]
    [InlineData("soft", CoreHintMode.Soft)]
    [InlineData("HARD", CoreHintMode.Hard)]
    [InlineData(" off ", CoreHintMode.Off)]
    [InlineData("bogus", CoreHintMode.Soft)]
    public void Mode_comes_from_the_environment_value(string? value, CoreHintMode expected)
    {
        Assert.Equal(expected, CoreHintPolicy.ParseMode(value));
    }

    [Fact]
    public void Ideal_processor_rotates_over_the_performance_cores_and_skips_logical_zero()
    {
        var best = CoreHintPolicy.SelectBestClass(CoreHintPolicy.ParseCpuSets(Concat(
            Record(1, 0, 0, 1), Record(2, 0, 1, 1), Record(3, 0, 2, 1), Record(4, 0, 16, 0))));
        var picks = new HashSet<byte>();
        for (var i = 0; i < 8; i++) picks.Add(CoreHintPolicy.PickIdeal(best, i).LogicalProcessorIndex);
        Assert.Equal(new HashSet<byte> { 1, 2 }, picks);
    }

    [Fact]
    public void Ideal_processor_falls_back_to_logical_zero_when_it_is_the_only_one()
    {
        var best = new List<CoreHintPolicy.CpuSet> { new(1, 0, 0, 1) };
        Assert.Equal((byte)0, CoreHintPolicy.PickIdeal(best, 5).LogicalProcessorIndex);
    }
}
