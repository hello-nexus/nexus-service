using System;
using System.Collections.Generic;
using System.IO;
using Nexus.Service.Lifecycle;
using Nexus.Service.Lighting;
using Nexus.Service.Lighting.Engine;
using Nexus.Service.Peripherals.Hyte.MiniHub;
using Nexus.Service.Peripherals.Hyte.Np50;
using Nexus.Service.Tests;
using Xunit;

namespace Nexus.Service.Tests.MiniHub;

public class MiniHubLightingFrameWriterTests
{
    private const string Serial = "MHTEST";
    private const string HubId = "minihub:" + Serial;

    private sealed class Fixture
    {
        public long NowMs;
        public readonly FakeTransport Transport = new();
        public readonly MiniHubHub Hub;
        public readonly LightingEngine Engine = new();
        public readonly InMemoryConfigStore Store = new();
        public readonly MiniHubLightingFrameWriter Writer;
        public readonly DeviceFrame Port1Frame = new(0, $"{HubId}:port1", ledCount: 4);
        public readonly DeviceFrame Port2Frame = new(1, $"{HubId}:port2", ledCount: 4);
        public readonly DeviceFrame Port3Frame = new(2, $"{HubId}:port3", ledCount: 0);
        public readonly DeviceFrame Port4Frame = new(3, $"{HubId}:port4", ledCount: 0);

        public Fixture(FeatureGates? gates = null)
        {
            Hub = new MiniHubHub(new FakeDiscovery(), _ => Transport);
            Assert.True(Hub.EnsureConnected());
            Engine.UpdateDevices(new[] { Port1Frame, Port2Frame, Port3Frame, Port4Frame });
            Writer = new MiniHubLightingFrameWriter(
                Engine,
                Hub,
                Store,
                new Np50IdentifyTracker(),
                gates ?? new FeatureGates(Store),
                () => NowMs);
        }

        public static void SetSolidColor(DeviceFrame frame, byte r, byte g, byte b)
        {
            var paint = frame.PaintBuffer;
            for (var i = 0; i + 2 < paint.Length; i += 3)
            {
                paint[i] = r;
                paint[i + 1] = g;
                paint[i + 2] = b;
            }
            frame.Publish();
        }
    }

    [Fact]
    public void Tick_GateOff_WritesNothing()
    {
        var f = new Fixture();
        f.Store.Update(s => s.Features.Lighting = false);

        f.Writer.Tick();

        Assert.Empty(f.Transport.Writes);
    }

    [Fact]
    public void Tick_StaticColor_WritesInitialCycleAndConfirm_ThenWritesNothingOnSubsequentTicks()
    {
        var f = new Fixture();
        Fixture.SetSolidColor(f.Port1Frame, 0xFF, 0x00, 0xD6);
        Fixture.SetSolidColor(f.Port2Frame, 0xFF, 0x00, 0xD6);

        f.Writer.Tick();
        Assert.Equal(4, f.Transport.Writes.Count);
        Assert.Equal(new[] { 1, 2, 3, 4 }, f.Transport.WrittenChannels);
        f.Transport.Clear();

        // One confirm cycle is emitted after MinStreamIntervalMs so static
        // latches survive any mode-transition hiccup, then all subsequent
        // unchanged ticks emit nothing.
        f.NowMs += MiniHubLightingFrameWriter.MinStreamIntervalMs;
        f.Writer.Tick();
        Assert.Equal(4, f.Transport.Writes.Count);
        Assert.Equal(new[] { 1, 2, 3, 4 }, f.Transport.WrittenChannels);

        f.Transport.Clear();
        f.NowMs += 200;
        f.Writer.Tick();
        f.NowMs += 200;
        f.Writer.Tick();

        Assert.Empty(f.Transport.Writes);
    }

    [Fact]
    public void Tick_SingleChannelChange_EmitsFullFourChannelCycleToCommitFirmwareLatch()
    {
        var f = new Fixture();
        Fixture.SetSolidColor(f.Port1Frame, 0x10, 0x20, 0x30);
        Fixture.SetSolidColor(f.Port2Frame, 0x40, 0x50, 0x60);

        f.Writer.Tick();
        Assert.Equal(4, f.Transport.Writes.Count);
        f.Transport.Clear();

        f.NowMs += MiniHubLightingFrameWriter.MinStreamIntervalMs;
        Fixture.SetSolidColor(f.Port1Frame, 0x99, 0x88, 0x77);
        f.Writer.Tick();

        Assert.Equal(4, f.Transport.Writes.Count);
        Assert.Equal(new[] { 1, 2, 3, 4 }, f.Transport.WrittenChannels);
    }

    [Fact]
    public void Tick_AllPortsUncontrolled_WritesNothing()
    {
        var f = new Fixture();
        Fixture.SetSolidColor(f.Port1Frame, 0xFF, 0x00, 0xD6);
        Fixture.SetSolidColor(f.Port2Frame, 0x00, 0x80, 0xFF);
        f.Store.Update(s =>
        {
            s.Devices.UncontrolledLightingDevices = new List<string>
            {
                $"{HubId}:port1",
                $"{HubId}:port2",
                $"{HubId}:port3",
                $"{HubId}:port4",
            };
        });

        f.Writer.Tick();

        Assert.Empty(f.Transport.Writes);
    }

    [Fact]
    public void Tick_AnimatedChangesWithinStreamInterval_ArePaced_UntilIntervalElapses_ExceptBlackout()
    {
        var f = new Fixture();
        Fixture.SetSolidColor(f.Port1Frame, 0x10, 0x00, 0x00);
        f.Writer.Tick();
        f.Transport.Clear();

        // 33 ms later (< 100 ms MinStreamIntervalMs): non-black animation frame is deferred.
        f.NowMs += 33;
        Fixture.SetSolidColor(f.Port1Frame, 0x20, 0x00, 0x00);
        f.Writer.Tick();
        Assert.Empty(f.Transport.Writes);

        // Once MinStreamIntervalMs has elapsed, the latest 4-channel cycle goes out.
        f.NowMs += MiniHubLightingFrameWriter.MinStreamIntervalMs;
        Fixture.SetSolidColor(f.Port1Frame, 0x30, 0x00, 0x00);
        f.Writer.Tick();
        Assert.Equal(4, f.Transport.Writes.Count);
        Assert.Equal(new[] { 1, 2, 3, 4 }, f.Transport.WrittenChannels);
        f.Transport.Clear();

        // Blackout / turn-off frame bypasses the stream interval immediately.
        f.NowMs += 10;
        Fixture.SetSolidColor(f.Port1Frame, 0x00, 0x00, 0x00);
        f.Writer.Tick();
        Assert.Equal(4, f.Transport.Writes.Count);
        Assert.Equal(new[] { 1, 2, 3, 4 }, f.Transport.WrittenChannels);
    }

    [Fact]
    public void Tick_AfterReconnectOrSetRgbControlMode_ResendsUnchangedFrames()
    {
        var f = new Fixture();
        Fixture.SetSolidColor(f.Port1Frame, 0xFF, 0x00, 0xD6);
        f.Writer.Tick();
        Assert.Equal(4, f.Transport.Writes.Count);
        f.Transport.Clear();

        // Re-asserting software RGB mode bumps ConnectEpoch so the writer re-pushes.
        Assert.True(f.Hub.SetRgbControlMode(MiniHubProtocol.RgbModeSoftware));
        f.Transport.Clear();

        f.Writer.Tick();
        Assert.Equal(4, f.Transport.Writes.Count);
        f.Transport.Clear();

        // Full disconnect + reconnect also bumps ConnectEpoch and re-pushes.
        f.Hub.Disconnect();
        f.Transport.ResetOpen();
        Assert.True(f.Hub.EnsureConnected());

        f.Writer.Tick();
        Assert.Equal(4, f.Transport.Writes.Count);
    }

    private sealed class FakeDiscovery : INp50PortDiscovery
    {
        public IReadOnlyList<Np50PortInfo> Discover() =>
            new[] { new Np50PortInfo { PortName = "COM_TEST", Serial = Serial } };
    }

    private sealed class FakeTransport : INp50Transport
    {
        public readonly List<byte[]> Writes = new();
        public bool Disposed { get; private set; }

        public bool IsOpen => !Disposed;
        public string Serial => MiniHubLightingFrameWriterTests.Serial;
        public void DiscardInput() { }

        public void Write(ReadOnlySpan<byte> data)
        {
            if (Disposed) throw new IOException("port closed");
            Writes.Add(data.ToArray());
        }

        public int Read(Span<byte> buffer, int timeoutMs) => 0;

        public void Dispose() => Disposed = true;

        public void ResetOpen() => Disposed = false;

        public void Clear() => Writes.Clear();

        public int[] WrittenChannels
        {
            get
            {
                var result = new List<int>(Writes.Count);
                foreach (var w in Writes)
                {
                    if (w.Length >= 4 && w[0] == 0xFF && w[1] == 0xEE && w[2] == 0x03)
                    {
                        result.Add(w[3]);
                    }
                }
                return result.ToArray();
            }
        }
    }
}
