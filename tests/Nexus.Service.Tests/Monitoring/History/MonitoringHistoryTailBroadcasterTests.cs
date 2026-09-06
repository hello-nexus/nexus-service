using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using Nexus.Service.Monitoring.History;
using Nexus.Service.Routes;
using Nexus.Service.Serialization;
using Nexus.Service.Sockets;
using Xunit;

namespace Nexus.Service.Tests.Monitoring.History;

public class MonitoringHistoryTailBroadcasterTests
{
    private static MetricSample FullScalarSample(long ts) =>
        new(ts, CpuPercent: 50, MemoryPercent: 60, NetInBytesPerSec: 1000, NetOutBytesPerSec: 500, CpuTempC: 55,
            Gpus: new[] { new GpuReading("gpu0", "Stub GPU", "LUID-1234", LoadPercent: 40, TempC: 65) },
            Fans: Array.Empty<FanReading>(),
            DiskReadBytesPerSec: 800, DiskWriteBytesPerSec: 400, Fps: 60);

    [Fact]
    public void OnSample_NoSubscribers_DoesNotBroadcast()
    {
        var hub = new MultiplexHub();
        var captured = new List<string>();
        hub.OnBroadcastForTest += (topic, _) => captured.Add(topic);
        var broadcaster = new MonitoringHistoryTailBroadcaster(hub);

        broadcaster.OnSample(FullScalarSample(1000), DateTime.UtcNow);

        Assert.Empty(captured);
    }

    [Fact]
    public void OnSample_WithSubscriber_BroadcastsOnePointPerSeries()
    {
        var hub = new MultiplexHub();
        var captured = new List<(string Topic, byte[] Payload)>();
        hub.OnBroadcastForTest += (topic, payload) => captured.Add((topic, payload.ToArray()));
        using var sub = hub.AddTestSubscription(PanelTopics.MonitoringHistoryTail);
        var broadcaster = new MonitoringHistoryTailBroadcaster(hub);
        var sample = FullScalarSample(1000);

        broadcaster.OnSample(sample, DateTime.UtcNow);

        var frame = captured.Single(c => c.Topic == PanelTopics.MonitoringHistoryTail);
        using var doc = JsonDocument.Parse(frame.Payload);
        Assert.Equal(PanelTopics.MonitoringHistoryTail, doc.RootElement.GetProperty("t").GetString());

        var response = JsonSerializer.Deserialize(
            doc.RootElement.GetProperty("d").GetRawText(), AppJsonContext.Default.MetricsHistoryResponse);
        Assert.NotNull(response);
        Assert.True(response!.Supported);
        Assert.Equal(1, response.StepSeconds);
        Assert.Equal(10, response.Series.Count); // cpu, memory, net-in, net-out, disk-read, disk-write, cpu-temp, fps, gpu, gpu-temp
        Assert.All(response.Series, s => Assert.Single(s.Points));
        Assert.All(response.Series, s => Assert.Equal(sample.TsSec * 1000, s.Points[0].T));

        var gpuSeries = response.Series.Single(s => s.Kind == "gpu");
        Assert.Equal("LUID-1234", gpuSeries.AdapterLuid);
    }
}
