using System;
using System.Collections.Generic;
using System.Linq;
using Nexus.Service.Cooling;
using Nexus.Service.Peripherals.Hyte.Np50;          // INp50Transport, Np50PortInfo
using Nexus.Service.Peripherals.Hyte.QSeriesCooler;
using Nexus.Service.Tests;
using RgbColor = Nexus.Service.Peripherals.Hyte.MiniHub.RgbColor;

namespace Nexus.Service.Tests.QSeriesCooler;

/// <summary>
/// Covers the Q-series coolant temperatures end to end: the Port-0 byte offsets the hub
/// consumes, and the curve/cooling surfaces the provider exposes them through.
/// </summary>
public class QSeriesCoolantTempTests
{
    // Port-0 bytes 5-6 = 2.261V = 50°C inlet, 7-8 = 2.755V = 25°C outlet (pump table).
    private const byte InHigh = 28, InLow = 6, OutHigh = 34, OutLow = 20;

    private static QSeriesCoolerHub NewConnectedHub(out ScriptedTransport transport)
    {
        var t = new ScriptedTransport();
        transport = t;
        var discovery = new FakeDiscovery(new QSeriesCoolerPort
        {
            PortName = "COM_TEST", Serial = "QTEST123", Variant = QSeriesCoolerProtocol.VariantQ60,
        });
        var hub = new QSeriesCoolerHub(discovery, _ => t);
        // Lighting is the cheapest path that opens the transport.
        hub.WriteLighting(new[] { new RgbColor(1, 2, 3) });
        return hub;
    }

    [Fact]
    public void PollTelemetry_reads_coolant_temps_from_port0_bytes_5_to_8()
    {
        var hub = NewConnectedHub(out var t);
        t.Port0Response = BuildPort0(InHigh, InLow, OutHigh, OutLow);

        Assert.True(hub.PollTelemetry());
        Assert.Equal(50f, hub.State.CoolantTempInC);
        Assert.Equal(25f, hub.State.CoolantTempOutC);
    }

    [Fact]
    public void GetTemperatureSources_exposes_both_coolant_probes_when_connected()
    {
        var hub = NewConnectedHub(out var t);
        t.Port0Response = BuildPort0(InHigh, InLow, OutHigh, OutLow);
        hub.PollTelemetry();

        var sources = new QSeriesCoolerCoolingProvider(hub, new InMemoryConfigStore()).GetTemperatureSources();

        Assert.Collection(sources,
            s =>
            {
                Assert.Equal("qseries:QTEST123:coolant-in", s.Id);
                Assert.Equal("Coolant in", s.Name);
                Assert.Equal("Cooler", s.Category);
                Assert.Equal(50f, s.Value);
                Assert.Equal("qseries:QTEST123", s.DeviceId);
            },
            s =>
            {
                Assert.Equal("qseries:QTEST123:coolant-out", s.Id);
                Assert.Equal(25f, s.Value);
            });
    }

    [Fact]
    public void ReadTemperature_resolves_a_coolant_id_and_ignores_foreign_ids()
    {
        var hub = NewConnectedHub(out var t);
        t.Port0Response = BuildPort0(InHigh, InLow, OutHigh, OutLow);
        hub.PollTelemetry();
        var provider = new QSeriesCoolerCoolingProvider(hub, new InMemoryConfigStore());

        Assert.Equal(50f, provider.ReadTemperature("qseries:QTEST123:coolant-in"));
        Assert.Equal(25f, provider.ReadTemperature("qseries:QTEST123:coolant-out"));
        Assert.Null(provider.ReadTemperature("qseries:QTEST123:nope"));
        Assert.Null(provider.ReadTemperature("np50:OTHER:legacy:cable"));
    }

    [Fact]
    public void A_probeless_reading_surfaces_no_source_at_all()
    {
        var hub = NewConnectedHub(out var t);
        // Both pairs zero = 0V, which saturates the table's hot end.
        t.Port0Response = BuildPort0(0, 0, 0, 0);
        hub.PollTelemetry();

        Assert.Null(hub.State.CoolantTempInC);
        Assert.Empty(new QSeriesCoolerCoolingProvider(hub, new InMemoryConfigStore()).GetTemperatureSources());
    }

    [Fact]
    public void GetAll_carries_the_coolant_temps_on_the_pump_device()
    {
        var hub = NewConnectedHub(out var t);
        t.Port0Response = BuildPort0(InHigh, InLow, OutHigh, OutLow);
        hub.PollTelemetry();

        var pump = new QSeriesCoolerCoolingProvider(hub, new InMemoryConfigStore()).GetAll()
            .Single().Devices.Single(d => d.Id == "qseries:QTEST123:pump");

        Assert.Equal(50f, pump.PumpTempIn);
        Assert.Equal(25f, pump.PumpTempOut);
        Assert.Equal(50f, pump.Temperature);
    }

    private static byte[] BuildPort0(byte inHigh, byte inLow, byte outHigh, byte outLow)
    {
        var resp = new byte[QSeriesCoolerProtocol.Port0ResponseLength];
        resp[0] = 0xFF; resp[1] = 0xCC;
        resp[5] = inHigh; resp[6] = inLow;
        resp[7] = outHigh; resp[8] = outLow;
        resp[9] = 1; resp[10] = 0;   // pump tach, so the parse succeeds
        return resp;
    }

    private sealed class FakeDiscovery : IQSeriesCoolerPortDiscovery
    {
        private readonly QSeriesCoolerPort[] _ports;
        public FakeDiscovery(params QSeriesCoolerPort[] ports) => _ports = ports;
        public IReadOnlyList<QSeriesCoolerPort> Discover() => _ports;
    }

    // Answers by the command last written rather than a fixed script, so connect-time
    // chatter can't shift the response the telemetry read expects.
    private sealed class ScriptedTransport : INp50Transport
    {
        private byte[]? _pending;
        public byte[]? Port0Response { get; set; }
        public bool IsOpen => true;
        public string Serial => "QTEST123";

        public void Write(ReadOnlySpan<byte> data)
        {
            var isPort0 = data.Length >= 4 && data[0] == 0xFF && data[1] == 0xCC && data[2] == 0x01 && data[3] == 0x00;
            _pending = isPort0 ? Port0Response : null;
        }

        public int Read(Span<byte> buffer, int timeoutMs)
        {
            if (_pending is null) return 0;
            var n = Math.Min(buffer.Length, _pending.Length);
            _pending.AsSpan(0, n).CopyTo(buffer);
            _pending = null;
            return n;
        }

        public void DiscardInput() { }
        public void Dispose() { }
    }
}
