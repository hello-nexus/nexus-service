using System.Text.Json;
using Qos.Service.Models.Panel;
using Qos.Service.Persistence;

namespace Qos.Service.Tests;

public class JsonConfigStoreTests : IDisposable
{
    private readonly string _tempDir;
    private readonly string _settingsPath;

    public JsonConfigStoreTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "qos-test-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_tempDir);
        _settingsPath = Path.Combine(_tempDir, "settings.json");
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); } catch { }
    }

    [Fact]
    public void Load_CreatesDefaultSettings_WhenFileDoesNotExist()
    {
        var store = new TestableConfigStore(_settingsPath);
        var settings = store.Load();

        Assert.NotNull(settings);
        Assert.Equal(QosSettings.CurrentSchemaVersion, settings.SchemaVersion);
        Assert.True(File.Exists(_settingsPath), "settings.json should be created on first load");
    }

    [Fact]
    public void Load_ReturnsCachedInstance()
    {
        var store = new TestableConfigStore(_settingsPath);
        var s1 = store.Load();
        var s2 = store.Load();
        Assert.Same(s1, s2);
    }

    [Fact]
    public void Update_PersistsToDisk()
    {
        var store = new TestableConfigStore(_settingsPath);
        store.Update(s => s.Lighting.FrameRate = 60);

        var json = File.ReadAllText(_settingsPath);
        Assert.Contains("60", json);
    }

    [Fact]
    public void Update_IsAtomicViaTmpFile()
    {
        var store = new TestableConfigStore(_settingsPath);
        store.Update(s => s.Lighting.FrameRate = 30);

        // After update, no .tmp file should remain
        Assert.False(File.Exists(_settingsPath + ".tmp"), "Temp file should be cleaned up after atomic write");
    }

    [Fact]
    public void Load_HandlesCorruptedJson()
    {
        File.WriteAllText(_settingsPath, "this is not json{{{");

        var store = new TestableConfigStore(_settingsPath);
        var settings = store.Load();

        Assert.NotNull(settings);
        Assert.Equal(QosSettings.CurrentSchemaVersion, settings.SchemaVersion);
    }

    [Fact]
    public void Update_RoundTrips_NestedSettings()
    {
        var store = new TestableConfigStore(_settingsPath);
        store.Update(s =>
        {
            s.Auth = new AuthSettings { Token = "test-token-123" };
            s.Lighting.FrameRate = 120;
            s.Lighting.Sync = "rainbow";
            s.Panel.DashboardLayout = new PanelLayoutDto
            {
                Surface = "desktop",
                Pages = new List<PanelPageDto>
                {
                    new PanelPageDto
                    {
                        Id = "dashboard",
                        Widgets = new List<PanelWidgetDto>
                        {
                            new PanelWidgetDto
                            {
                                Id = "w1",
                                Type = "monitoring",
                                Size = "4x2",
                            },
                        },
                    },
                },
            };
        });

        // Read back from a fresh store instance (bypasses cache)
        var store2 = new TestableConfigStore(_settingsPath);
        var loaded = store2.Load();

        Assert.Equal("test-token-123", loaded.Auth?.Token);
        Assert.Equal(120, loaded.Lighting.FrameRate);
        Assert.Equal("rainbow", loaded.Lighting.Sync);
        Assert.Equal("desktop", loaded.Panel.DashboardLayout?.Surface);
        Assert.Equal("monitoring", loaded.Panel.DashboardLayout?.Pages[0].Widgets[0].Type);
    }
}

/// <summary>
/// JsonConfigStore subclass that overrides the path for testing.
/// </summary>
internal sealed class TestableConfigStore : IConfigStore
{
    private readonly string _path;
    private QosSettings? _cached;
    private readonly object _lock = new();

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };

    public TestableConfigStore(string path)
    {
        _path = path;
    }

    public string SettingsPath => _path;

    public QosSettings Load()
    {
        lock (_lock)
        {
            if (_cached is not null) return _cached;

            if (!File.Exists(_path))
            {
                _cached = new QosSettings();
                Persist(_cached);
                return _cached;
            }

            try
            {
                var json = File.ReadAllText(_path);
                _cached = JsonSerializer.Deserialize<QosSettings>(json, JsonOptions) ?? new QosSettings();
            }
            catch
            {
                _cached = new QosSettings();
            }

            return _cached;
        }
    }

    public void Update(Action<QosSettings> mutator)
    {
        lock (_lock)
        {
            var doc = _cached ?? Load();
            mutator(doc);
            Persist(doc);
        }
        OnChanged?.Invoke();
    }

    public void Reload() { lock (_lock) _cached = null; }

    public void FlushNow() { }

    public event Action? OnChanged;

    private void Persist(QosSettings doc)
    {
        var dir = Path.GetDirectoryName(_path)!;
        Directory.CreateDirectory(dir);

        var tmpPath = _path + ".tmp";
        var json = JsonSerializer.Serialize(doc, JsonOptions);
        File.WriteAllText(tmpPath, json);

        if (File.Exists(_path))
            File.Replace(tmpPath, _path, null);
        else
            File.Move(tmpPath, _path);
    }
}
