using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Nexus.Service.Models.Devices;
using Nexus.Service.Serialization;
using Nexus.Service.Sockets;
using Microsoft.Extensions.Hosting;

namespace Nexus.Service.Devices;

/// <summary>
/// Subscription-aware push for the curated device list (/devices/all) and the
/// raw USB enumeration (/devices/usb/all). Clients subscribe to the
/// <c>devices</c> topic; this background service owns the single poll, diffs
/// the snapshot, and only fires a frame when the list actually changed (or the
/// first subscriber arrives).
/// </summary>
public sealed class DeviceBroadcaster : BackgroundService
{
    public const string Topic = "devices";
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(5);

    private readonly DeviceManager _manager;
    private readonly MultiplexHub _hub;
    private string _lastFingerprint = "";
    private readonly object _fingerprintLock = new();

    public DeviceBroadcaster(DeviceManager manager, MultiplexHub hub)
    {
        _manager = manager;
        _hub = hub;
        _hub.OnTopicFirstSubscriber += topic =>
        {
            if (topic == Topic) BroadcastNow();
        };
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                if (_hub.TopicHasSubscribers(Topic))
                {
                    BroadcastNow();
                }
            }
            catch
            {
                // Best-effort; never let a polled enumeration crash the host.
            }

            try
            {
                await Task.Delay(PollInterval, stoppingToken);
            }
            catch (TaskCanceledException) { break; }
        }
    }

    /// <summary>Force an immediate fingerprint check and broadcast. Routes that mutate device state call this so subscribers refetch without waiting out the poll cadence.</summary>
    public void BroadcastNow()
    {
        List<DeviceListItem> curated;
        List<UsbDeviceDetail> usb;
        try
        {
            curated = _manager.GetAll();
#if DEV_TOOLS
            // The fingerprint reads the simulated list too, so starting or stopping a sim broadcasts.
            Nexus.Service.Dev.DevSimEvents.Current?.ApplyDevices(curated);
#endif
            usb = _manager.GetUsbDevices();
        }
        catch
        {
            return;
        }

        // Called from both the poll loop and the /devices/control route thread,
        // so guard the dedup fingerprint against a torn read/write.
        var fingerprint = ComputeFingerprint(curated, usb);
        lock (_fingerprintLock)
        {
            if (fingerprint == _lastFingerprint)
                return;
            _lastFingerprint = fingerprint;
        }

        var frame = new Models.Panel.DevicesChangedFrame
        {
            Revision = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
        };
        var env = WsEnvelope.Build(Topic, frame, AppJsonContext.Default.DevicesChangedFrame);
        _ = _hub.BroadcastTopicAsync(Topic, env);
    }

    private static string ComputeFingerprint(List<DeviceListItem> curated, List<UsbDeviceDetail> usb)
    {
        var sb = new StringBuilder(256);
        sb.Append("c:");
        foreach (var d in curated.OrderBy(d => d.Id, StringComparer.Ordinal))
        {
            sb.Append(d.Id).Append(d.Connected ? '1' : '0').Append(d.NexusControlEnabled ? '1' : '0').Append(d.FirmwareVersion ?? "").Append(';');
        }
        sb.Append("|u:");
        foreach (var d in usb.OrderBy(d => d.HardwareId ?? "", StringComparer.Ordinal))
        {
            sb.Append(d.VendorId).Append(d.ProductId).Append(d.Name).Append(';');
        }
        return sb.ToString();
    }
}
