namespace Nexus.Service.Sensors;

/// <summary>
/// The display text for a sensor reading, keyed by the sensor type name. Kept free of
/// LibreHardwareMonitor types so it compiles and tests on every platform.
/// </summary>
internal static class SensorValueText
{
    internal static string Format(float value, string type) => type switch
    {
        "Temperature" => $"{value:F1} °C",
        "Load" or "Control" or "Level" => $"{value:F1}%",
        "Clock" => $"{value:F0} MHz",
        "Voltage" => $"{value:F3} V",
        "Current" => $"{value:F3} A",
        "Fan" => $"{value:F0} RPM",
        "Power" => $"{value:F1} W",
        "Energy" => $"{value:F0} mWh",
        "Throughput" => FormatThroughput(value),
        "TimeSpan" => FormatTimeSpan(value),
        _ => $"{value:F1}",
    };

    private static string FormatThroughput(float bytesPerSec)
    {
        if (bytesPerSec <= 0)
            return "0 B/s";
        if (bytesPerSec >= 1024 * 1024 * 1024)
            return $"{bytesPerSec / 1024.0 / 1024.0 / 1024.0:F2} GB/s";
        if (bytesPerSec >= 1024 * 1024)
            return $"{bytesPerSec / 1024.0 / 1024.0:F2} MB/s";
        if (bytesPerSec >= 1024)
            return $"{bytesPerSec / 1024.0:F1} KB/s";
        return $"{bytesPerSec:F0} B/s";
    }

    // LHM reports Battery "Remaining Time" and PSU "Uptime"/"Total uptime" in seconds.
    private static string FormatTimeSpan(float seconds)
    {
        if (seconds <= 0)
            return "0";
        if (seconds < 3600)
            return $"{seconds / 60:F0}m";
        if (seconds < 86400)
            return $"{seconds / 3600:F1}h";
        return $"{seconds / 86400:F1}d";
    }
}
