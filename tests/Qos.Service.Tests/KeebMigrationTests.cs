using Qos.Service.Persistence;

namespace Qos.Service.Tests;

/// <summary>
/// v5 → v6 schema migration: the key-reactive overlay fields move out of
/// <c>keeb.firmwareLighting</c> into a sibling <c>keeb.passiveLighting</c>
/// block. Verifies the move is value-preserving, idempotent, and leaves
/// unrelated firmware-lighting fields where they were.
/// </summary>
public class KeebMigrationTests : IDisposable
{
    private readonly string _tempDir;
    private readonly string _settingsPath;

    public KeebMigrationTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "qos-keeb-mig-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_tempDir);
        _settingsPath = Path.Combine(_tempDir, "settings.json");
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); } catch { }
    }

    [Fact]
    public void V5ToV6_SplitsKeyReactiveOutOfFirmwareLighting()
    {
        var v5Json = """
        {
          "schemaVersion": 5,
          "keeb": {
            "rotaryLeft": "VolumeAdjustment",
            "rotaryRight": "BrightnessAdjustment",
            "rotarySensitivity": "Balanced",
            "firmwareLighting": {
              "animationMode": "Rainbow",
              "speed": "Energetic",
              "direction": "LeftToRight",
              "brightness": 60,
              "keyReactive": true,
              "keyReactiveMask": true,
              "keyReactiveMode": "Ripple",
              "keyReactiveColor": { "r": 200, "g": 100, "b": 50, "a": 255 }
            }
          }
        }
        """;
        File.WriteAllText(_settingsPath, v5Json);

        var store = new JsonConfigStore(_settingsPath);
        var s = store.Load();
        try
        {
            Assert.Equal(QosSettings.CurrentSchemaVersion, s.SchemaVersion);

            // Firmware lighting fields preserved on their original block.
            Assert.Equal("Rainbow", s.Keeb.FirmwareLighting.AnimationMode);
            Assert.Equal("Energetic", s.Keeb.FirmwareLighting.Speed);
            Assert.Equal("LeftToRight", s.Keeb.FirmwareLighting.Direction);
            Assert.Equal(60, s.Keeb.FirmwareLighting.Brightness);

            // Key-reactive fields moved off firmware lighting.
            Assert.True(s.Keeb.PassiveLighting.KeyReactive);
            Assert.True(s.Keeb.PassiveLighting.KeyReactiveMask);
            Assert.Equal("Ripple", s.Keeb.PassiveLighting.KeyReactiveMode);
            Assert.Equal(200, s.Keeb.PassiveLighting.KeyReactiveColor.R);
            Assert.Equal(100, s.Keeb.PassiveLighting.KeyReactiveColor.G);
            Assert.Equal(50, s.Keeb.PassiveLighting.KeyReactiveColor.B);
            Assert.Equal(255, s.Keeb.PassiveLighting.KeyReactiveColor.A);
        }
        finally
        {
            store.Dispose();
        }
    }

    [Fact]
    public void V5ToV6_NoKeyReactiveFields_LeavesPassiveLightingAtDefaults()
    {
        // A v5 file that doesn't carry the key-reactive fields at all — the
        // migration should bump the version and leave passiveLighting defaulted.
        var v5Json = """
        {
          "schemaVersion": 5,
          "keeb": {
            "firmwareLighting": {
              "animationMode": "Static",
              "brightness": 80
            }
          }
        }
        """;
        File.WriteAllText(_settingsPath, v5Json);

        var store = new JsonConfigStore(_settingsPath);
        var s = store.Load();
        try
        {
            Assert.Equal(QosSettings.CurrentSchemaVersion, s.SchemaVersion);
            Assert.Equal("Static", s.Keeb.FirmwareLighting.AnimationMode);
            Assert.False(s.Keeb.PassiveLighting.KeyReactive);
            Assert.False(s.Keeb.PassiveLighting.KeyReactiveMask);
        }
        finally
        {
            store.Dispose();
        }
    }

    [Fact]
    public void V6Idempotent_LoadingV6FileLeavesItUnchanged()
    {
        var v6Json = """
        {
          "schemaVersion": 6,
          "keeb": {
            "firmwareLighting": { "animationMode": "Wave", "brightness": 70 },
            "passiveLighting": {
              "keyReactive": true,
              "keyReactiveMask": false,
              "keyReactiveMode": "SingleKey",
              "keyReactiveColor": { "r": 12, "g": 34, "b": 56, "a": 255 }
            }
          }
        }
        """;
        File.WriteAllText(_settingsPath, v6Json);

        var store = new JsonConfigStore(_settingsPath);
        var s = store.Load();
        try
        {
            Assert.Equal(6, s.SchemaVersion);
            Assert.Equal("Wave", s.Keeb.FirmwareLighting.AnimationMode);
            Assert.True(s.Keeb.PassiveLighting.KeyReactive);
            Assert.Equal("SingleKey", s.Keeb.PassiveLighting.KeyReactiveMode);
            Assert.Equal(12, s.Keeb.PassiveLighting.KeyReactiveColor.R);
        }
        finally
        {
            store.Dispose();
        }
    }
}
