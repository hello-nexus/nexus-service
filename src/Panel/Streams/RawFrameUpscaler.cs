using System;
using System.Runtime.InteropServices;
using Nexus.Service.Platform;

namespace Nexus.Service.Panel.Streams;

/// <summary>
/// Scales raw BGRA frames the overlay rendered below native back up to the panel's native
/// size before they are queued, so transports and drivers only ever see native frames.
/// Nearest neighbour: the cheapest resample, and at half scale it doubles each pixel.
/// </summary>
public sealed class RawFrameUpscaler
{
    private readonly string _sessionId;
    private readonly int _srcWidth;
    private readonly int _srcHeight;
    private readonly int _dstWidth;
    private readonly int _dstHeight;
    private readonly int[] _srcColumn;
    private bool _mismatchLogged;

    private RawFrameUpscaler(string sessionId, int srcWidth, int srcHeight, int dstWidth, int dstHeight)
    {
        _sessionId = sessionId;
        _srcWidth = srcWidth;
        _srcHeight = srcHeight;
        _dstWidth = dstWidth;
        _dstHeight = dstHeight;
        _srcColumn = new int[dstWidth];
        for (var x = 0; x < dstWidth; x++)
        {
            _srcColumn[x] = SourceIndex(x, srcWidth, dstWidth);
        }
    }

    /// <summary>Null unless the profile renders raw frames below native.</summary>
    public static RawFrameUpscaler? For(string sessionId, StreamedPanelProfile profile)
    {
        if (profile.Codec != StreamCodec.RawBgra || profile.RenderScale >= 1.0)
        {
            return null;
        }
        var render = profile.Dpr * profile.RenderScale;
        return new RawFrameUpscaler(
            sessionId,
            StreamedPanelProfile.FramePixels(profile.CssWidth, render),
            StreamedPanelProfile.FramePixels(profile.CssHeight, render),
            StreamedPanelProfile.FramePixels(profile.CssWidth, profile.Dpr),
            StreamedPanelProfile.FramePixels(profile.CssHeight, profile.Dpr));
    }

    public int SourceBytes => _srcWidth * _srcHeight * 4;

    public int TargetBytes => _dstWidth * _dstHeight * 4;

    /// <summary>
    /// Returns the native-size frame and releases <paramref name="frame"/>. A frame that is not
    /// exactly one render-size image is dropped (null): handed on, it would desync the
    /// transport's frame boundaries.
    /// </summary>
    public StreamFrame? Apply(StreamFrame frame)
    {
        var flags = frame.Flags;
        if (frame.Bytes.Length != SourceBytes)
        {
            if (!_mismatchLogged)
            {
                _mismatchLogged = true;
                ServiceLog.Warn($"[streamed-panel] session={_sessionId} frame of {frame.Bytes.Length} bytes, expected {SourceBytes} ({_srcWidth}x{_srcHeight}); dropped");
            }
            frame.Release();
            return null;
        }
        var payload = StreamFrame.Pool.Rent(TargetBytes);
        Scale(frame.Bytes, payload.AsSpan(0, TargetBytes));
        frame.Release();
        return new StreamFrame
        {
            Flags = flags,
            Payload = payload,
            Length = TargetBytes,
            Pooled = true,
        };
    }

    internal void Scale(ReadOnlySpan<byte> source, Span<byte> target)
    {
        var src = MemoryMarshal.Cast<byte, uint>(source);
        var dst = MemoryMarshal.Cast<byte, uint>(target);
        var previousSourceRow = -1;
        for (var y = 0; y < _dstHeight; y++)
        {
            var row = dst.Slice(y * _dstWidth, _dstWidth);
            var sourceRow = SourceIndex(y, _srcHeight, _dstHeight);
            if (sourceRow == previousSourceRow)
            {
                dst.Slice((y - 1) * _dstWidth, _dstWidth).CopyTo(row);
                continue;
            }
            previousSourceRow = sourceRow;
            var from = src.Slice(sourceRow * _srcWidth, _srcWidth);
            for (var x = 0; x < row.Length; x++)
            {
                row[x] = from[_srcColumn[x]];
            }
        }
    }

    // The source pixel whose centre is nearest the target pixel's centre.
    private static int SourceIndex(int target, int sourceLength, int targetLength) =>
        (int)(((2L * target + 1) * sourceLength) / (2L * targetLength));
}
