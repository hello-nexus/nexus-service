using System;
using System.Collections.Generic;

namespace Nexus.Service.Peripherals.SteelSeries;

/// <summary>How a model packs the 128x40 bitmap into the 640 frame bytes.</summary>
public enum ApexOledLayout
{
    /// <summary>Row-major, sixteen bytes per row, leftmost pixel in the high bit.</summary>
    Rows,

    /// <summary>Eight-row pages, one byte per column, topmost pixel in the low bit.</summary>
    Columns,
}

public sealed record ApexOledModel(int ProductId, string Name, ApexOledLayout Layout);

/// <summary>
/// Wire format of the Apex Pro TKL keyboards' OLED. Protocol details from a patch by spudge;
/// only the 2023 model's packing has been seen working on hardware.
/// </summary>
public static class ApexOledProtocol
{
    public const int VendorId = 0x1038;
    public const int Width = 128;
    public const int Height = 40;
    public const int FrameBytes = Width * Height / 8;

    /// <summary>HID usage page of the interface that takes screen commands.</summary>
    public const int ControlUsagePage = 0xFFC0;

    public const int FeatureReportLength = 644;
    public const int OutputReportLength = 65;

    private const byte OledCommand = 0x1F;
    private const byte LiveFrame = 0x81;
    private const byte HandBack = 0x82;

    private static readonly ApexOledModel[] AllModels =
    {
        new(0x1628, "SteelSeries Apex Pro TKL 2023", ApexOledLayout.Rows),
        new(0x1642, "SteelSeries Apex Pro TKL Gen 3", ApexOledLayout.Columns),
    };

    public static IReadOnlyList<ApexOledModel> Models => AllModels;

    public static int[] ProductIds { get; } = Array.ConvertAll(AllModels, m => m.ProductId);

    public static ApexOledModel? ModelFor(int productId)
    {
        foreach (var model in AllModels)
        {
            if (model.ProductId == productId)
            {
                return model;
            }
        }
        return null;
    }

    /// <summary>Packs <see cref="Width"/> x <see cref="Height"/> row-major lit flags into the model's frame bytes.</summary>
    public static byte[] Pack(ReadOnlySpan<bool> lit, ApexOledLayout layout)
    {
        if (lit.Length != Width * Height)
        {
            throw new ArgumentException($"expected {Width * Height} pixels, got {lit.Length}", nameof(lit));
        }
        var packed = new byte[FrameBytes];
        for (var y = 0; y < Height; y++)
        {
            for (var x = 0; x < Width; x++)
            {
                if (!lit[y * Width + x])
                {
                    continue;
                }
                if (layout == ApexOledLayout.Rows)
                {
                    packed[y * (Width / 8) + x / 8] |= (byte)(0x80 >> (x % 8));
                }
                else
                {
                    packed[y / 8 * Width + x] |= (byte)(1 << (y % 8));
                }
            }
        }
        return packed;
    }

    /// <summary>The feature report that shows one packed frame: report id, command, subcommand, bitmap.</summary>
    public static byte[] FrameReport(ReadOnlySpan<byte> packed)
    {
        if (packed.Length != FrameBytes)
        {
            throw new ArgumentException($"expected {FrameBytes} bytes, got {packed.Length}", nameof(packed));
        }
        var report = new byte[FeatureReportLength];
        report[1] = OledCommand;
        report[2] = LiveFrame;
        packed.CopyTo(report.AsSpan(3));
        return report;
    }

    /// <summary>The output report that gives the screen back to the keyboard firmware.</summary>
    public static byte[] HandBackReport()
    {
        var report = new byte[OutputReportLength];
        report[1] = OledCommand;
        report[2] = HandBack;
        return report;
    }
}
