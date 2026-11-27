using System;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Nexus.Service.Tests.SmartLights;

/// <summary>
/// In-process Govee LAN device emulator: one loopback UDP socket plays the
/// device's scan (:4001) and control (:4003) roles; replies go back to the
/// request's source endpoint (the client sends everything from its fixed
/// listener socket, so this matches the real fixed-port-4002 behavior).
/// Records every command for assertions.
/// </summary>
internal sealed class FakeGoveeDevice : IDisposable
{
    private readonly Socket _sock;
    private readonly CancellationTokenSource _cts = new();

    public FakeGoveeDevice(string sku = "H619A", string deviceId = "1F:80:C5:32:32:36:72:4E")
    {
        Sku = sku;
        DeviceId = deviceId;
        _sock = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        _sock.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        Port = ((IPEndPoint)_sock.LocalEndPoint!).Port;
        _ = Task.Run(() => RunAsync(_cts.Token));
    }

    public int Port { get; }
    public string Sku { get; }
    public string DeviceId { get; }

    /// <summary>False simulates "LAN Control" disabled: scans and status
    /// requests go unanswered (control packets are still recorded).</summary>
    public volatile bool LanEnabled = true;

    /// <summary>False simulates filtered scan traffic while devStatus still
    /// answers (the SKU-less pairing fallback path).</summary>
    public volatile bool ScanEnabled = true;

    public volatile bool OnOff = true;
    private int _statusQueries;
    public int StatusQueries => Volatile.Read(ref _statusQueries);
    public volatile int Brightness = 100;

    public ConcurrentQueue<bool> Turns { get; } = new();
    public ConcurrentQueue<int> Brightnesses { get; } = new();
    public ConcurrentQueue<(int R, int G, int B, int Kelvin)> Colors { get; } = new();
    /// <summary>Decoded razer "pt" packets, in arrival order.</summary>
    public ConcurrentQueue<byte[]> RazerPackets { get; } = new();

    private async Task RunAsync(CancellationToken ct)
    {
        var buf = new byte[8192];
        while (!ct.IsCancellationRequested)
        {
            SocketReceiveFromResult res;
            try
            {
                res = await _sock.ReceiveFromAsync(buf, SocketFlags.None,
                    new IPEndPoint(IPAddress.Any, 0), ct);
            }
            catch (OperationCanceledException) { break; }
            catch (ObjectDisposedException) { break; }
            catch (SocketException) { continue; }

            if (res.ReceivedBytes <= 0 || res.RemoteEndPoint is not IPEndPoint from) continue;
            try { Handle(Encoding.UTF8.GetString(buf, 0, res.ReceivedBytes), from); }
            catch { /* malformed test payload - ignore */ }
        }
    }

    private void Handle(string json, IPEndPoint from)
    {
        using var doc = JsonDocument.Parse(json);
        var msg = doc.RootElement.GetProperty("msg");
        var cmd = msg.GetProperty("cmd").GetString();
        var data = msg.GetProperty("data");

        switch (cmd)
        {
            case "scan":
                if (LanEnabled && ScanEnabled)
                {
                    var scanData = $$"""{"ip":"127.0.0.1","device":"{{DeviceId}}","sku":"{{Sku}}","bleVersionHard":"3.01.01","bleVersionSoft":"1.03.01","wifiVersionHard":"1.00.10","wifiVersionSoft":"1.02.03"}""";
                    Reply(from, """{"msg":{"cmd":"scan","data":""" + scanData + "}}");
                }
                break;
            case "devStatus":
                Interlocked.Increment(ref _statusQueries);
                if (LanEnabled)
                {
                    var statusData = $$"""{"onOff":{{(OnOff ? 1 : 0)}},"brightness":{{Brightness}},"color":{"r":255,"g":0,"b":0},"colorTemInKelvin":0}""";
                    Reply(from, """{"msg":{"cmd":"devStatus","data":""" + statusData + "}}");
                }
                break;
            case "turn":
                var on = data.GetProperty("value").GetInt32() == 1;
                OnOff = on;
                Turns.Enqueue(on);
                break;
            case "brightness":
                var bri = data.GetProperty("value").GetInt32();
                Brightness = bri;
                Brightnesses.Enqueue(bri);
                break;
            case "colorwc":
                var color = data.GetProperty("color");
                Colors.Enqueue((
                    color.GetProperty("r").GetInt32(),
                    color.GetProperty("g").GetInt32(),
                    color.GetProperty("b").GetInt32(),
                    data.GetProperty("colorTemInKelvin").GetInt32()));
                break;
            case "razer":
                RazerPackets.Enqueue(Convert.FromBase64String(data.GetProperty("pt").GetString()!));
                break;
        }
    }

    private void Reply(IPEndPoint to, string json)
    {
        try { _sock.SendTo(Encoding.UTF8.GetBytes(json), to); }
        catch (SocketException) { }
        catch (ObjectDisposedException) { }
    }

    public void Dispose()
    {
        _cts.Cancel();
        _cts.Dispose();
        _sock.Dispose();
    }
}
