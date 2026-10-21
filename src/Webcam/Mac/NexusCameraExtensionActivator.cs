using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Nexus.Service.Webcam.Mac;

/// <summary>
/// P/Invoke wrapper over the flat C exports of the bundled activator dylib
/// (Bundled/macos/camera-helper/activator.swift), which submits the
/// OSSystemExtensionRequest in-process (Bundle.main must be Nexus.app for
/// sysextd to accept it; see camera-extension NOTES.md). The two exports use
/// distinct raw code scales, each mapped to <see cref="CameraExtensionState"/>;
/// both block their calling thread until a delegate callback or timeout.
/// </summary>
[SupportedOSPlatform("macos")]
internal sealed partial class NexusCameraExtensionActivator : ICameraExtensionActivator
{
    private const string LibraryName = "nexus-camera-activator";

    internal static string BundledDylibPath =>
        Path.Combine(AppContext.BaseDirectory, "nexus-camera-activator.dylib");

    static NexusCameraExtensionActivator()
    {
        // The dylib lives next to the service binary, outside default probing.
        // Only one resolver may be registered per assembly; this never collides
        // with NexusVCamControl's because each registers only on its own OS.
        NativeLibrary.SetDllImportResolver(typeof(NexusCameraExtensionActivator).Assembly, static (name, _, _) =>
            name == LibraryName && File.Exists(BundledDylibPath)
                ? NativeLibrary.Load(BundledDylibPath)
                : IntPtr.Zero);
    }

    public CameraExtensionState GetStatus()
    {
        EnsureDylib();
        return MapStatus(ExtensionStatus());
    }

    public CameraExtensionState Activate()
    {
        EnsureDylib();
        return MapActivate(ExtensionActivate());
    }

    private static void EnsureDylib()
    {
        if (!File.Exists(BundledDylibPath))
        {
            throw new InvalidOperationException(
                $"camera activator dylib missing at {BundledDylibPath}; this build does not include the camera extension, so the virtual camera is unavailable");
        }
    }

    // Raw code contracts with activator.swift (status and activate use
    // different scales); unknown values and the timeout/busy/error sentinels
    // collapse to Failed.
    private static CameraExtensionState MapStatus(int raw) => raw switch
    {
        0 => CameraExtensionState.Installed,
        1 => CameraExtensionState.PendingApproval,
        2 => CameraExtensionState.Disabled,
        3 => CameraExtensionState.NotInstalled,
        _ => CameraExtensionState.Failed,
    };

    private static CameraExtensionState MapActivate(int raw) => raw switch
    {
        0 => CameraExtensionState.Installed,
        1 => CameraExtensionState.RebootRequired,
        2 => CameraExtensionState.PendingApproval,
        _ => CameraExtensionState.Failed,
    };

    [LibraryImport(LibraryName, EntryPoint = "nexus_camera_extension_status")]
    private static partial int ExtensionStatus();

    [LibraryImport(LibraryName, EntryPoint = "nexus_camera_extension_activate")]
    private static partial int ExtensionActivate();
}
