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
    private static HardwareSensor Sensor(string name, float value, string units, string formatted) => new()
    {
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

    /// <summary>A source string with no decimal point converts with zero decimal places.</summary>
    [Fact]
    public void ResolveValueText_TemperatureUnderF_NoSourceDecimal_ConvertsWithZeroDecimals()
    {
        var sensor = Sensor("Core", 20f, "°C", "20 °C");
        Assert.Equal("68 °F", DeckMonitoringFormat.ResolveValueText(sensor, "f", "system"));
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
