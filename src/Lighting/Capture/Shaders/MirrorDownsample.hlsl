// Point-samples the desktop duplication surface into a dstW x dstH target using
// the same integer mapping as DesktopRotation.MapToSurface, so the output bytes
// match the CPU path exactly.
// Rebuild the embedded bytecode (Windows SDK x64 fxc):
//   fxc /nologo /O3 /T vs_4_0 /E VS /Fo MirrorDownsample.vs.cso MirrorDownsample.hlsl
//   fxc /nologo /O3 /T ps_4_0 /E PS /Fo MirrorDownsample.ps.cso MirrorDownsample.hlsl
cbuffer Params : register(b0)
{
    uint dstW;
    uint dstH;
    uint surfW;
    uint surfH;
    uint rotation;
    uint3 pad;
};

Texture2D<float4> src : register(t0);

float4 VS(uint id : SV_VertexID) : SV_Position
{
    float2 uv = float2((id << 1) & 2, id & 2);
    return float4(uv * float2(2, -2) + float2(-1, 1), 0, 1);
}

float4 PS(float4 pos : SV_Position) : SV_Target
{
    uint x = (uint)pos.x;
    uint y = (uint)pos.y;
    uint sx;
    uint sy;
    if (rotation == 2)
    {
        sx = y * surfW / dstH;
        sy = (dstW - 1 - x) * surfH / dstW;
    }
    else if (rotation == 3)
    {
        sx = (dstW - 1 - x) * surfW / dstW;
        sy = (dstH - 1 - y) * surfH / dstH;
    }
    else if (rotation == 4)
    {
        sx = (dstH - 1 - y) * surfW / dstH;
        sy = x * surfH / dstW;
    }
    else
    {
        sx = x * surfW / dstW;
        sy = y * surfH / dstH;
    }
    return src.Load(int3(sx, sy, 0));
}
