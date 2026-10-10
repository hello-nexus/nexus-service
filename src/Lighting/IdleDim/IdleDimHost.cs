using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;

namespace Nexus.Service.Lighting.IdleDim;

/// <summary>Starts the idle-dim controller with the host and releases any dim on the way out.</summary>
public sealed class IdleDimHost : IHostedService
{
    private readonly IdleDimController _controller;

    public IdleDimHost(IdleDimController controller) => _controller = controller;

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _controller.Start();
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _controller.Stop();
        return Task.CompletedTask;
    }
}
