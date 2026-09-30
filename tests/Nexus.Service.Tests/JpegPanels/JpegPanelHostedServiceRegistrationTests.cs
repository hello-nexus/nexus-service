using System.Linq;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Nexus.Service.Devices;
using Nexus.Service.Devices.Detection;
using Nexus.Service.Peripherals.Hid;
using Nexus.Service.Peripherals.JpegPanels;
using Xunit;

namespace Nexus.Service.Tests.JpegPanels;

/// <summary>
/// One connection worker must run per model. AddHostedService de-duplicates by
/// implementation type, so the per-model loop has to register IHostedService directly or
/// every model after the first silently gets no worker and its panel is never claimed.
/// </summary>
public class JpegPanelHostedServiceRegistrationTests
{
    [Fact]
    public void Every_model_gets_its_own_worker()
    {
        var services = new ServiceCollection();
        var gate = new DeviceControlGate(new InMemoryConfigStore());
        IHidEnumerator hid = new StubHidEnumerator();
        var presence = new HardwarePresence(new StubUsbEnumerator());

        foreach (var model in JpegPanelModel.All)
        {
            var hub = new JpegPanelHub(model);
            services.AddSingleton<IHostedService>(_ => new JpegPanelConnectionWorker(hid, hub, gate, presence));
        }

        using var provider = services.BuildServiceProvider();
        var workers = provider.GetServices<IHostedService>().OfType<JpegPanelConnectionWorker>().ToArray();

        Assert.Equal(JpegPanelModel.All.Length, workers.Length);
    }

    [Fact]
    public void AddHostedService_would_collapse_them_which_is_why_it_is_not_used()
    {
        var services = new ServiceCollection();
        var gate = new DeviceControlGate(new InMemoryConfigStore());
        IHidEnumerator hid = new StubHidEnumerator();
        var presence = new HardwarePresence(new StubUsbEnumerator());

        foreach (var model in JpegPanelModel.All)
        {
            var hub = new JpegPanelHub(model);
            services.AddHostedService(_ => new JpegPanelConnectionWorker(hid, hub, gate, presence));
        }

        using var provider = services.BuildServiceProvider();
        var workers = provider.GetServices<IHostedService>().OfType<JpegPanelConnectionWorker>().ToArray();

        Assert.Single(workers);
    }
}
