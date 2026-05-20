using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Qos.Service.Devices;

namespace Qos.Service.Peripherals.Keeb;

/// <summary>
/// Polls the device list periodically and opens/closes the
/// <see cref="KeebSession"/> when the Keeb TKL is plugged or unplugged.
///
/// Why a poll instead of a true hot-swap event: <see cref="DeviceManager"/>
/// is poll-based itself (5 s interval via <c>DeviceBroadcaster</c>); piggy-backing
/// on the same cadence avoids a second USB enumeration on every tick.
/// </summary>
public sealed class KeebHotswapHost : BackgroundService
{
    private static readonly System.TimeSpan PollInterval = System.TimeSpan.FromSeconds(5);

    private readonly KeebSession _session;
    private readonly DeviceManager _devices;
    private readonly ILogger<KeebHotswapHost> _log;
    private bool _wasConnected;

    public KeebHotswapHost(KeebSession session, DeviceManager devices, ILogger<KeebHotswapHost> log)
    {
        _session = session;
        _devices = devices;
        _log = log;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var entry = System.Linq.Enumerable.FirstOrDefault(_devices.GetAll(), d => d.Id == "keeb");
                var connected = entry?.Connected ?? false;

                if (connected && !_wasConnected)
                {
                    if (_session.TryOpen())
                    {
                        _log.LogInformation("Keeb TKL opened on hot-swap (fw {Fw}).", _session.Keeb?.FirmwareVersion ?? "?");
                        _wasConnected = true;
                    }
                }
                else if (!connected && _wasConnected)
                {
                    _session.Close();
                    _log.LogInformation("Keeb TKL closed (device disappeared).");
                    _wasConnected = false;
                }
            }
            catch (System.Exception e)
            {
                _log.LogError(e, "KeebHotswapHost iteration failed");
            }

            try { await Task.Delay(PollInterval, stoppingToken); }
            catch (TaskCanceledException) { break; }
        }
    }
}
