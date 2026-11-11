using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace Nexus.Service.Persistence;

/// <summary>Which sensor a rule watches. Id is tried first; Name, Type and ParentName let the engine find the sensor again when its id changed (hardware swap, driver update).</summary>
public sealed class SensorRef
{
    /// <summary>Resolver category: cpu, gpu, memory, motherboard, storage, psu, cooler, ...</summary>
    public string Category { get; set; } = "";
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    /// <summary>Sensor type name, e.g. "Temperature", "Current".</summary>
    public string Type { get; set; } = "";
    public string ParentName { get; set; } = "";
}

/// <summary>
/// What a built-in rule reads right now, in the sensor picker's identity shape. Wire only: never stored, rebuilt on every list.
/// <c>sensor</c> kind: one picker sensor, <see cref="Sensor"/> set. <c>average</c> kind: the mean of <see cref="Sensors"/> (several memory-module sensors), no single id.
/// <c>drive</c> kind: the drive's SMART health reading, which is not a picker sensor, so <see cref="Sensor"/> is null.
/// </summary>
public sealed class BuiltInSource
{
    public string Kind { get; set; } = "sensor";
    public SensorRef? Sensor { get; set; }
    public List<SensorRef> Sensors { get; set; } = new();
    /// <summary>The device the reading belongs to: CPU model, GPU name, drive name, or the memory label.</summary>
    public string HardwareName { get; set; } = "";
}

/// <summary>One user threshold rule. Low and High are in the sensor's native unit (temperature is always Celsius); a null bound is not checked, and a rule with neither bound only tracks the sensor's history.</summary>
public sealed class SensorAlertRule
{
    public string Id { get; set; } = "";
    public bool Enabled { get; set; } = true;
    public SensorRef Sensor { get; set; } = new();
    // Never omitted on the wire: the contract says null, not absent.
    [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    public double? Low { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    public double? High { get; set; }
    public int RaiseAfterSeconds { get; set; } = 5;
    /// <summary>Wire and downgrade compatibility only: always 5, any input is ignored. The engine clears after a fixed delay.</summary>
    public int ClearAfterSeconds { get => 5; set { } }
    /// <summary>"watch" | "act".</summary>
    public string Severity { get; set; } = "watch";
    public bool NotifyDesktop { get; set; } = true;
    /// <summary>Wire and downgrade compatibility only: always true, any input is ignored.</summary>
    public bool ShowInHealth { get => true; set { } }
    /// <summary>Wire and downgrade compatibility only: always 0, any input is ignored. An alert notifies once per episode.</summary>
    public int RepeatMinutes { get => 0; set { } }
    /// <summary>Null for a user rule; else the component this built-in rule watches: "cpuTemp" | "gpuTemp" | "storageTemp" | "ramTemp". A built-in keeps a fixed id and sensor, can be turned off but not deleted.</summary>
    public string? BuiltIn { get; set; }
    /// <summary>Built-in only, wire only: the exact reading behind it right now. Null for a user rule and in storage.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public BuiltInSource? Source { get; set; }

    public SensorAlertRule Clone()
    {
        var copy = (SensorAlertRule)MemberwiseClone();
        copy.Sensor = new SensorRef
        {
            Category = Sensor.Category,
            Id = Sensor.Id,
            Name = Sensor.Name,
            Type = Sensor.Type,
            ParentName = Sensor.ParentName,
        };
        return copy;
    }

    /// <summary>True when nothing that shapes evaluation differs. The sensor id is ignored: the engine rewrites it when it self-heals, and that must not reset a running episode.</summary>
    public bool SameConfig(SensorAlertRule other) =>
        Enabled == other.Enabled
        && Low == other.Low
        && High == other.High
        && RaiseAfterSeconds == other.RaiseAfterSeconds
        && Severity == other.Severity
        && NotifyDesktop == other.NotifyDesktop
        && BuiltIn == other.BuiltIn
        && Sensor.Category == other.Sensor.Category
        && Sensor.Name == other.Sensor.Name
        && Sensor.Type == other.Sensor.Type
        && Sensor.ParentName == other.Sensor.ParentName;
}
