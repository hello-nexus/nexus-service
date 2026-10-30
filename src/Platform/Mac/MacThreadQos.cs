using System;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Nexus.Service.Platform.Mac;

/// <summary>
/// Raises the calling thread to the user-interactive QoS class so Apple Silicon schedules it on
/// performance cores. A paced encode thread sleeps between frames and a default-class thread can
/// otherwise be placed on efficiency cores, where a JPEG encode takes several times longer.
/// </summary>
[SupportedOSPlatform("macos")]
internal static partial class MacThreadQos
{
    private const uint QosClassUserInteractive = 0x21;

    public static void ApplyToCurrentThread()
    {
        try
        {
            pthread_set_qos_class_self_np(QosClassUserInteractive, 0);
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
        }
    }

    [LibraryImport("libSystem.dylib")]
    private static partial int pthread_set_qos_class_self_np(uint qosClass, int relativePriority);
}
