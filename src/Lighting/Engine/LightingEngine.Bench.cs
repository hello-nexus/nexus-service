namespace Nexus.Service.Lighting.Engine;

public sealed partial class LightingEngine
{
    /// <summary>Samples <paramref name="canvas"/> onto <paramref name="devices"/> exactly as a live
    /// tick does, minus pins. For the lighting bench, which drives the render itself.</summary>
    internal void SampleForBench(DeviceFrame[] devices, CanvasBuffer canvas)
    {
        _sampleCanvas = canvas;
        try
        {
            SampleDevicesFromCanvas(devices, honorPins: false);
        }
        finally
        {
            _sampleCanvas = _canvas;
        }
    }
}
