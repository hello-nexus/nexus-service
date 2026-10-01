using System;
using System.Collections.Generic;
using Nexus.Service.Devices;
using Nexus.Service.Devices.Detection;
using Nexus.Service.Devices.Firmware;
using Nexus.Service.Plugins;

namespace Nexus.Service.Tests;

/// <summary>A FirmwareFlasher with no targets, no USB devices and no dfu-util binary.</summary>
internal static class TestFirmwareFlasher
{
    public static FirmwareFlasher Create()
    {
        var gate = new FlashGate();
        var catalog = new BundledFirmwareCatalog();
        var dfu = new DfuUtil("dfu-util");
        var winusb = new WinUsbDriverInstaller();
        var monitor = new DfuRecoveryMonitor(new HardwarePresence(new EmptyUsb()), gate, dfu, winusb, catalog);
        return new FirmwareFlasher(catalog, Array.Empty<IDfuFlashTarget>(), winusb, dfu, new PluginProviderRegistry(), gate, monitor);
    }

    private sealed class EmptyUsb : IUsbEnumerator
    {
        public List<UsbDeviceEntry> Enumerate() => new();
    }
}
