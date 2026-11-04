using Nexus.Service.Deck;
using Nexus.Service.Models.Sensors;
using Xunit;

namespace Nexus.Service.Tests.StreamDeck;

/// <summary>
/// Pins DeckMonitoringFormat's parity with nexus-web's DeckMonitoringCell:
/// labelForDevice (MonitoringWidget.tsx) for the default top label, and
/// formatScaledDataValue (sensorValueFormat.ts) for the byte-unit ladder the
/// device tile previously did not apply.
/// </summary>
public class DeckMonitoringFormatTests
{
    private static HardwareSensor Sensor(string name, float value, string units, string formatted, string type = "") => new()
    {
        Type = type,
        Id = "test/sensor",
        Name = name,
        Value = value,
        Units = units,
        Formatted = formatted,
        Parent = new SensorParent(),
    };

    [Theory]
    [InlineData("cpu", "CPU Total", "CPU Total")]
    [InlineData("cpu", "Total", "CPU Total")]
    [InlineData("cpu", "CPU", "CPU")]
    [InlineData("gpu", "Core Clock", "GPU Core Clock")]
    [InlineData("gpu", "GPU Core Clock", "GPU Core Clock")]
    [InlineData("memory", "Used", "Memory Used")]
    [InlineData("memory", "Memory Used", "Memory Used")]
    [InlineData("motherboard", "System", "System")]
    [InlineData("storage", "Drive C", "Drive C")]
    [InlineData("quick", "Anything", "Anything")]
    // network is the fourth prefixed category (sensorNames.ts' DEVICE_PREFIXES);
    // the rest of the wider deck set names itself and stays untouched.
    [InlineData("network", "Total", "Network Total")]
    [InlineData("network", "Network Total", "Network Total")]
    [InlineData("smart", "Temperature", "Temperature")]
    [InlineData("memoryModule", "Temperature", "Temperature")]
    [InlineData("fps", "FPS", "FPS")]
    public void ResolveLabel_SensorNameSet_MirrorsLabelForDeviceAndPrefixedSensorLabel(string category, string sensorName, string expected)
    {
        Assert.Equal(expected, DeckMonitoringFormat.ResolveLabel(category, sensorName));
    }

    [Theory]
    [InlineData("quick", "Quick")]
    [InlineData("cpu", "CPU")]
    [InlineData("gpu", "GPU")]
    [InlineData("memory", "RAM")]
    [InlineData("motherboard", "MB")]
    [InlineData("storage", "Storage")]
    [InlineData("memoryModule", "DIMM")]
    [InlineData("smart", "SMART")]
    [InlineData("network", "Network")]
    [InlineData("fps", "FPS")]
    [InlineData("battery", "BATT")]
    [InlineData("cooler", "COOL")]
    [InlineData("psu", "PSU")]
    [InlineData("embeddedController", "EC")]
    public void ResolveLabel_NoSensorName_FallsBackToTheCategoryDisplayName(string category, string expected)
    {
        Assert.Equal(expected, DeckMonitoringFormat.ResolveLabel(category, ""));
    }

    /// <summary>A Data-type sensor whose raw GB value crosses the 1024 boundary scales up to TB, matching the web cell.</summary>
    [Fact]
    public void ResolveValueText_LargeGbValue_ScalesUpToTb()
    {
        var sensor = Sensor("Used", 1214.96f, "GB", "1214.96 GB");
        Assert.Equal("1.2 TB", DeckMonitoringFormat.ResolveValueText(sensor, "c", "system"));
    }

    [Fact]
    public void ResolveValueText_SmallGbValue_StaysInGbWithOneDecimal()
    {
        var sensor = Sensor("Used", 4.5f, "GB", "4.50 GB");
        Assert.Equal("4.5 GB", DeckMonitoringFormat.ResolveValueText(sensor, "c", "system"));
    }

    [Fact]
    public void ResolveValueText_IntegerScaledValue_DropsTheDecimal()
    {
        var sensor = Sensor("Used", 2048f, "MB", "2048.00 MB");
        Assert.Equal("2 GB", DeckMonitoringFormat.ResolveValueText(sensor, "c", "system"));
    }

    [Fact]
    public void ResolveValueText_Percent_FallsBackToTheServiceFormattedString()
    {
        var sensor = Sensor("Total", 42f, "%", "42.0%");
        Assert.Equal("42.0%", DeckMonitoringFormat.ResolveValueText(sensor, "c", "system"));
    }

    [Fact]
    public void ResolveValueText_TemperatureUnderC_FallsBackToTheServiceFormattedString()
    {
        var sensor = Sensor("Core", 65.3f, "°C", "65.3 °C");
        Assert.Equal("65.3 °C", DeckMonitoringFormat.ResolveValueText(sensor, "c", "system"));
    }

    [Fact]
    public void ResolveValueText_Rpm_FallsBackToTheServiceFormattedString()
    {
        var sensor = Sensor("Fan 1", 1200f, "RPM", "1200 RPM");
        Assert.Equal("1200 RPM", DeckMonitoringFormat.ResolveValueText(sensor, "c", "system"));
    }

    [Fact]
    public void ResolveValueText_UnknownUnit_FallsBackToTheServiceFormattedString()
    {
        var sensor = Sensor("Clock", 4200f, "MHz", "4200 MHz");
        Assert.Equal("4200 MHz", DeckMonitoringFormat.ResolveValueText(sensor, "c", "system"));
    }

    /// <summary>0 C is freezing: converts to exactly 32 F, matching convertTemperature.</summary>
    [Fact]
    public void ResolveValueText_TemperatureUnderF_Freezing_ConvertsToThirtyTwo()
    {
        var sensor = Sensor("Core", 0f, "°C", "0.0 °C");
        Assert.Equal("32.0 °F", DeckMonitoringFormat.ResolveValueText(sensor, "f", "system"));
    }

    /// <summary>100 C is boiling: converts to exactly 212 F.</summary>
    [Fact]
    public void ResolveValueText_TemperatureUnderF_Boiling_ConvertsToTwoHundredTwelve()
    {
        var sensor = Sensor("Core", 100f, "°C", "100.0 °C");
        Assert.Equal("212.0 °F", DeckMonitoringFormat.ResolveValueText(sensor, "f", "system"));
    }

    /// <summary>Fractional Celsius: keeps the source string's one decimal place, matching formatSensorValue's toFixed(decimals) rule.</summary>
    [Fact]
    public void ResolveValueText_TemperatureUnderF_Fractional_KeepsSourceDecimalPrecision()
    {
        var sensor = Sensor("Core", 65.3f, "°C", "65.3 °C");
        Assert.Equal("149.5 °F", DeckMonitoringFormat.ResolveValueText(sensor, "f", "system"));
    }

    /// <summary>The shared rule gives Fahrenheit one decimal regardless of the source string's precision.</summary>
    [Fact]
    public void ResolveValueText_TemperatureUnderF_NoSourceDecimal_UsesTheRuleDecimals()
    {
        var sensor = Sensor("Core", 20f, "°C", "20 °C");
        Assert.Equal("68.0 °F", DeckMonitoringFormat.ResolveValueText(sensor, "f", "system"));
    }

    [Theory]
    // Default: the rule's per-type decimals replace the provider string's number.
    [InlineData("Temperature", 65.34f, "°C", "65 °C", false, "c", "65.3 °C")]
    [InlineData("Power", 12.34f, "W", "12 W", false, "c", "12.3 W")]
    [InlineData("Load", 42.26f, "%", "42%", false, "c", "42.3%")]
    [InlineData("Voltage", 0.85f, "V", "0.9 V", false, "c", "0.850 V")]
    [InlineData("Current", 1.5f, "A", "2 A", false, "c", "1.500 A")]
    [InlineData("FrameTime", 16.66f, "ms", "16.66 ms", false, "c", "16.7 ms")]
    [InlineData("Clock", 4200.4f, "MHz", "4200.4 MHz", false, "c", "4200 MHz")]
    // Whole numbers: rounded types drop decimals (half away from zero), precision types keep theirs.
    [InlineData("Temperature", 65.5f, "°C", "65.5 °C", true, "c", "66 °C")]
    [InlineData("Temperature", -0.5f, "°C", "-0.5 °C", true, "c", "-1 °C")]
    [InlineData("Temperature", 65.3f, "°C", "65.3 °C", true, "f", "150 °F")]
    [InlineData("Power", 12.5f, "W", "12.5 W", true, "c", "13 W")]
    [InlineData("Load", 42.4f, "%", "42.4%", true, "c", "42%")]
    [InlineData("Flow", 99.6f, "L/h", "99.6 L/h", true, "c", "100 L/h")]
    [InlineData("Voltage", 0.85f, "V", "0.850 V", true, "c", "0.850 V")]
    [InlineData("Current", 1.5f, "A", "1.500 A", true, "c", "1.500 A")]
    [InlineData("FrameTime", 16.66f, "ms", "16.7 ms", true, "c", "16.7 ms")]
    public void ResolveValueText_FollowsTheSharedDecimalsRule(string type, float value, string units, string formatted, bool whole, string tempUnit, string expected)
    {
        var sensor = Sensor("S", value, units, formatted, type);
        Assert.Equal(expected, DeckMonitoringFormat.ResolveValueText(sensor, tempUnit, "system", whole));
    }

    // Expected strings come from node (c * 9 / 5 + 32).toFixed(d).
    [Theory]
    [InlineData(30.25f, false, "86.5 °F")]
    [InlineData(45.25f, false, "113.5 °F")]
    [InlineData(50.25f, false, "122.5 °F")]
    [InlineData(60.75f, false, "141.3 °F")]
    [InlineData(30.25f, true, "86 °F")]
    [InlineData(45.25f, true, "113 °F")]
    [InlineData(60.75f, true, "141 °F")]
    public void ResolveValueText_Fahrenheit_MatchesTheWebAtQuarterDegrees(float celsius, bool whole, string expected)
    {
        var sensor = Sensor("S", celsius, "°C", "x", "Temperature");
        Assert.Equal(expected, DeckMonitoringFormat.ResolveValueText(sensor, "f", "system", whole));
    }

    [Fact]
    public void ResolveValueText_RuleType_EmptyFormatted_StaysEmpty()
    {
        var sensor = Sensor("S", 12f, "W", "", "Power");
        Assert.Equal("", DeckMonitoringFormat.ResolveValueText(sensor, "c", "system", true));
    }

    [Fact]
    public void ResolveValueText_WholeNumbers_DoesNotTouchDataScaling()
    {
        var sensor = Sensor("Used", 4.5f, "GB", "4.50 GB", "Data");
        Assert.Equal("4.5 GB", DeckMonitoringFormat.ResolveValueText(sensor, "c", "system", true));
    }

    /// <summary>A tempUnit compare is case-insensitive, matching the "f"/"c" persisted values regardless of case.</summary>
    [Fact]
    public void ResolveValueText_TemperatureUnderF_IsCaseInsensitive()
    {
        var sensor = Sensor("Core", 0f, "°C", "0.0 °C");
        Assert.Equal("32.0 °F", DeckMonitoringFormat.ResolveValueText(sensor, "F", "system"));
    }

    /// <summary>A non-Celsius unit (already Fahrenheit-reporting hardware, or any other unit) never gets converted, matching isCelsiusUnit's exact-match gate.</summary>
    [Fact]
    public void ResolveValueText_TemperatureUnderF_NonCelsiusUnit_NeverConverts()
    {
        var sensor = Sensor("Core", 150f, "°F", "150.0 °F");
        Assert.Equal("150.0 °F", DeckMonitoringFormat.ResolveValueText(sensor, "f", "system"));
    }

    /// <summary>"comma" number format re-separates the converted temperature's decimal point, mirroring localizeNumbers.</summary>
    [Fact]
    public void ResolveValueText_TemperatureUnderF_CommaNumberFormat_SwapsTheDecimalSeparator()
    {
        var sensor = Sensor("Core", 65.3f, "°C", "65.3 °C");
        Assert.Equal("149,5 °F", DeckMonitoringFormat.ResolveValueText(sensor, "f", "comma"));
    }

    /// <summary>"comma" number format re-separates a non-temperature formatted string too.</summary>
    [Fact]
    public void ResolveValueText_Percent_CommaNumberFormat_SwapsTheDecimalSeparator()
    {
        var sensor = Sensor("Total", 42f, "%", "42.5%");
        Assert.Equal("42,5%", DeckMonitoringFormat.ResolveValueText(sensor, "c", "comma"));
    }

    /// <summary>"system" has no OS/browser locale to resolve service-side, so it stays the untouched "dot" style rather than reading machine culture.</summary>
    [Fact]
    public void ResolveValueText_SystemNumberFormat_LeavesTheDotStyleUntouched()
    {
        var sensor = Sensor("Total", 42f, "%", "42.5%");
        Assert.Equal("42.5%", DeckMonitoringFormat.ResolveValueText(sensor, "c", "system"));
    }

    /// <summary>"dot" is already the source format: a no-op, same as "system".</summary>
    [Fact]
    public void ResolveValueText_DotNumberFormat_IsANoOp()
    {
        var sensor = Sensor("Total", 42f, "%", "42.5%");
        Assert.Equal("42.5%", DeckMonitoringFormat.ResolveValueText(sensor, "c", "dot"));
    }
}
