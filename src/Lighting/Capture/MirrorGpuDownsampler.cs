#if WINDOWS
using System;
using System.Runtime.InteropServices;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;

namespace Nexus.Service.Lighting.Capture;

/// <summary>
/// Shrinks a duplicated desktop surface to the mirror grid on the GPU with the
/// same point sampling and rotation mapping as the CPU path, so only the small
/// target crosses to system memory. Resources are reused across frames and
/// rebuilt only when the grid or the surface size changes.
/// </summary>
internal sealed class MirrorGpuDownsampler : IDisposable
{
    [StructLayout(LayoutKind.Sequential)]
    private struct Params
    {
        public uint DstW, DstH, SurfW, SurfH, Rotation, Pad0, Pad1, Pad2;
    }

    private readonly ID3D11Device _device;
    private readonly ID3D11DeviceContext _context;
    private readonly ID3D11VertexShader _vs;
    private readonly ID3D11PixelShader _ps;
    private readonly ID3D11RasterizerState _raster;
    private readonly ID3D11Buffer _cbuffer;
    private ID3D11Texture2D? _target;
    private ID3D11RenderTargetView? _targetView;
    private ID3D11Texture2D? _staging;
    private ID3D11Texture2D? _copySrc;
    private ID3D11ShaderResourceView? _copySrcView;
    private Params _params;
    private int _dstW, _dstH, _copyW, _copyH;
    private bool _disposed;

    public int Width => _dstW;
    public int Height => _dstH;

    public MirrorGpuDownsampler(ID3D11Device device, ID3D11DeviceContext context)
    {
        _device = device;
        _context = context;
        _vs = device.CreateVertexShader(MirrorDownsampleShaders.Vertex);
        _ps = device.CreatePixelShader(MirrorDownsampleShaders.Pixel);
        _raster = device.CreateRasterizerState(new RasterizerDescription(CullMode.None, FillMode.Solid));
        _cbuffer = device.CreateBuffer(new BufferDescription
        {
            ByteWidth = (uint)Marshal.SizeOf<Params>(),
            Usage = ResourceUsage.Default,
            BindFlags = BindFlags.ConstantBuffer,
        });
    }

    /// <summary>
    /// Draws <paramref name="surface"/> into the grid target and copies it to
    /// the readback texture. Queues GPU work only; the caller Flushes.
    /// </summary>
    public void Downsample(ID3D11Texture2D surface, int dstW, int dstH, int surfW, int surfH, int rotation)
    {
        EnsureTargets(dstW, dstH);
        var desc = surface.Description;
        if (desc.Format != Format.B8G8R8A8_UNorm)
        {
            throw new InvalidOperationException($"unsupported duplication format {desc.Format}");
        }
        var next = new Params
        {
            DstW = (uint)dstW, DstH = (uint)dstH, SurfW = (uint)surfW, SurfH = (uint)surfH, Rotation = (uint)rotation,
        };
        if (next.DstW != _params.DstW || next.DstH != _params.DstH || next.SurfW != _params.SurfW
            || next.SurfH != _params.SurfH || next.Rotation != _params.Rotation)
        {
            _params = next;
            _context.UpdateSubresource(in _params, _cbuffer);
        }

        // Not cached: a duplication surface must not be referenced past ReleaseFrame.
        ID3D11ShaderResourceView? direct = null;
        var srv = _copySrcView;
        try
        {
            if ((desc.BindFlags & BindFlags.ShaderResource) != 0)
            {
                direct = _device.CreateShaderResourceView(surface);
                srv = direct;
            }
            else
            {
                EnsureCopySource((int)desc.Width, (int)desc.Height);
                _context.CopyResource(_copySrc!, surface);
                srv = _copySrcView;
            }

            _context.IASetPrimitiveTopology(PrimitiveTopology.TriangleList);
            _context.VSSetShader(_vs);
            _context.PSSetShader(_ps);
            _context.PSSetConstantBuffer(0, _cbuffer);
            _context.PSSetShaderResource(0, srv!);
            _context.RSSetState(_raster);
            _context.RSSetViewport(0f, 0f, dstW, dstH);
            _context.OMSetRenderTargets(_targetView!);
            _context.Draw(3, 0);
            // Unbind so the duplication surface is not held as a pipeline input after the frame is released.
            _context.ClearState();
            _context.CopyResource(_staging!, _target!);
        }
        finally
        {
            direct?.Dispose();
        }
    }

    /// <summary>Copies the readback texture into <paramref name="dst"/> as RGB24 (3 bytes per pixel).</summary>
    public unsafe bool ReadRgb(byte[] dst, int dstW, int dstH)
    {
        if (_staging is null || dstW != _dstW || dstH != _dstH || dst.Length < dstW * dstH * 3)
        {
            return false;
        }
        var mapped = _context.Map(_staging, 0, MapMode.Read);
        try
        {
            var rowPitch = (int)mapped.RowPitch;
            var src = (byte*)mapped.DataPointer;
            fixed (byte* dstPtr = dst)
            {
                for (var y = 0; y < dstH; y++)
                {
                    var row = src + y * rowPitch;
                    var d = dstPtr + y * dstW * 3;
                    for (var x = 0; x < dstW; x++)
                    {
                        d[x * 3] = row[x * 4 + 2];
                        d[x * 3 + 1] = row[x * 4 + 1];
                        d[x * 3 + 2] = row[x * 4];
                    }
                }
            }
            return true;
        }
        finally { _context.Unmap(_staging, 0); }
    }

    private void EnsureTargets(int dstW, int dstH)
    {
        if (_target is not null && dstW == _dstW && dstH == _dstH)
        {
            return;
        }
        _targetView?.Dispose();
        _target?.Dispose();
        _staging?.Dispose();
        _target = _device.CreateTexture2D(new Texture2DDescription
        {
            Width = (uint)dstW, Height = (uint)dstH, MipLevels = 1, ArraySize = 1,
            Format = Format.B8G8R8A8_UNorm, SampleDescription = new SampleDescription(1, 0),
            Usage = ResourceUsage.Default, BindFlags = BindFlags.RenderTarget,
        });
        _targetView = _device.CreateRenderTargetView(_target);
        _staging = _device.CreateTexture2D(new Texture2DDescription
        {
            Width = (uint)dstW, Height = (uint)dstH, MipLevels = 1, ArraySize = 1,
            Format = Format.B8G8R8A8_UNorm, SampleDescription = new SampleDescription(1, 0),
            Usage = ResourceUsage.Staging, CPUAccessFlags = CpuAccessFlags.Read,
        });
        _dstW = dstW;
        _dstH = dstH;
    }

    private void EnsureCopySource(int w, int h)
    {
        if (_copySrc is not null && w == _copyW && h == _copyH)
        {
            return;
        }
        _copySrcView?.Dispose();
        _copySrc?.Dispose();
        _copySrc = _device.CreateTexture2D(new Texture2DDescription
        {
            Width = (uint)w, Height = (uint)h, MipLevels = 1, ArraySize = 1,
            Format = Format.B8G8R8A8_UNorm, SampleDescription = new SampleDescription(1, 0),
            Usage = ResourceUsage.Default, BindFlags = BindFlags.ShaderResource,
        });
        _copySrcView = _device.CreateShaderResourceView(_copySrc);
        _copyW = w;
        _copyH = h;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;
        _copySrcView?.Dispose();
        _copySrc?.Dispose();
        _targetView?.Dispose();
        _target?.Dispose();
        _staging?.Dispose();
        _cbuffer.Dispose();
        _raster.Dispose();
        _ps.Dispose();
        _vs.Dispose();
    }
}
#endif
