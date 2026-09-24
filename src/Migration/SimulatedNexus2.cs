#if DEV_TOOLS
using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using Nexus.Service.Models.Panel;
using Nexus.Service.Panel;
using Nexus.Service.Persistence;

namespace Nexus.Service.Migration;

/// <summary>
/// Fake Nexus 2 install, active when NEXUS_SIM_NEXUS2=1: the detector
/// reports a running eligible install whose close/disable-autostart actions
/// flip in-memory state, the config reader serves an embedded realistic
/// config.json whose gallery/wallpaper files are materialized under the
/// Nexus data root, and the seeder mints y70/q60 panel records (only when
/// the surface has none) so the returning-user screen, preview, and apply
/// run the real import pipeline end to end on machines with no Nexus 2.
/// </summary>
internal static class SimulatedNexus2
{
    public static readonly bool Enabled =
        Environment.GetEnvironmentVariable("NEXUS_SIM_NEXUS2") == "1";

    /// <summary>Materializes the fake install dir (idempotent) and returns its path.</summary>
    public static string EnsureSourceDir()
    {
        var dir = Path.Combine(NexusDataPaths.NexusRoot(), "sim-nexus2");
        var media = Path.Combine(dir, "q60", "web", "user-media");
        Directory.CreateDirectory(media);
        var jpeg = Convert.FromBase64String(JpegBase64);
        WriteIfAbsent(Path.Combine(dir, "existing-a.jpg"), jpeg);
        WriteIfAbsent(Path.Combine(dir, "existing-b.jpg"), jpeg);
        WriteIfAbsent(Path.Combine(media, "sunset.jpg"), jpeg);
        return dir;
    }

    private static void WriteIfAbsent(string path, byte[] bytes)
    {
        if (!File.Exists(path))
        {
            File.WriteAllBytes(path, bytes);
        }
    }

    // Solid accent-color JPEG so imported wallpapers render as real images.
    private const string JpegBase64 =
        "/9j/4AAQSkZJRgABAQAASABIAAD/4QBMRXhpZgAATU0AKgAAAAgAAYdpAAQAAAABAAAAGgAAAAAAA6ABAAMAAAABAAEAAKACAAQA" +
        "AAABAAAAQKADAAQAAAABAAAAQAAAAAD/7QA4UGhvdG9zaG9wIDMuMAA4QklNBAQAAAAAAAA4QklNBCUAAAAAABDUHYzZjwCyBOmA" +
        "CZjs+EJ+/8AAEQgAQABAAwEiAAIRAQMRAf/EAB8AAAEFAQEBAQEBAAAAAAAAAAABAgMEBQYHCAkKC//EALUQAAIBAwMCBAMFBQQE" +
        "AAABfQECAwAEEQUSITFBBhNRYQcicRQygZGhCCNCscEVUtHwJDNicoIJChYXGBkaJSYnKCkqNDU2Nzg5OkNERUZHSElKU1RVVldY" +
        "WVpjZGVmZ2hpanN0dXZ3eHl6g4SFhoeIiYqSk5SVlpeYmZqio6Slpqeoqaqys7S1tre4ubrCw8TFxsfIycrS09TV1tfY2drh4uPk" +
        "5ebn6Onq8fLz9PX29/j5+v/EAB8BAAMBAQEBAQEBAQEAAAAAAAABAgMEBQYHCAkKC//EALURAAIBAgQEAwQHBQQEAAECdwABAgMR" +
        "BAUhMQYSQVEHYXETIjKBCBRCkaGxwQkjM1LwFWJy0QoWJDThJfEXGBkaJicoKSo1Njc4OTpDREVGR0hJSlNUVVZXWFlaY2RlZmdo" +
        "aWpzdHV2d3h5eoKDhIWGh4iJipKTlJWWl5iZmqKjpKWmp6ipqrKztLW2t7i5usLDxMXGx8jJytLT1NXW19jZ2uLj5OXm5+jp6vLz" +
        "9PX29/j5+v/bAEMAAgICAgICAwICAwUDAwMFBgUFBQUGCAYGBgYGCAoICAgICAgKCgoKCgoKCgwMDAwMDA4ODg4ODw8PDw8PDw8P" +
        "D//bAEMBAgICBAQEBwQEBxALCQsQEBAQEBAQEBAQEBAQEBAQEBAQEBAQEBAQEBAQEBAQEBAQEBAQEBAQEBAQEBAQEBAQEP/dAAQA" +
        "BP/aAAwDAQACEQMRAD8A8Hooor/RwzCiiigAooooAKKKKAP/0PB6KKK/0cMwooooAKKKKACiiigD/9Hweiiiv9HDMKKKKACiiigA" +
        "ooooA//S8Hooor/RwzCiiigAooooAKKKKAP/2Q==";

    // Mirrors the shape of a real Nexus 2 config.json (same content as the
    // Nexus2MigrationServiceTests fixture): an active profile with a two-page
    // Y70 layout + dock, Q60 pages + media background, gallery sources, and
    // the settings/rotation/y70 roots the translators read.
    internal const string ConfigJson = """
        {
          "profiles": [
            {
              "id": "profile-1",
              "name": "Profile 1",
              "active": true,
              "widgets": {
                "faces": {
                  "y70": {
                    "nexus": true,
                    "hideDock": false,
                    "theme": {
                      "opacity": 0.7,
                      "transparent": false,
                      "transparentDock": "opaque",
                      "text": "191, 221, 255",
                      "primary": { "main": "36, 35, 36", "light": "36, 35, 36", "dark": "35, 34, 35" },
                      "accent": { "main": "97, 74, 223", "light": "97, 74, 223", "dark": "96, 73, 222" }
                    },
                    "controlCenter": { "timeFormat": "24" },
                    "bgName": "cyber",
                    "bg": "",
                    "bgDisabled": false,
                    "page": "page-1",
                    "dock": [
                      {
                        "id": "dock-clock",
                        "type": "clock",
                        "size": "1x1",
                        "design": "flip",
                        "timeFormat": "12",
                        "seconds": true,
                        "timezone": "America/New_York",
                        "displayTimezone": true,
                        "position": 0,
                        "positionHorizontal": 0,
                        "isImmersive": false
                      }
                    ],
                    "pages": [
                      {
                        "id": "page-1",
                        "type": "page",
                        "widgets": [
                          {
                            "id": "w-clock",
                            "type": "clock",
                            "size": "4x2",
                            "design": "flip",
                            "timeFormat": "12",
                            "seconds": true,
                            "timezone": "America/New_York",
                            "displayTimezone": true,
                            "position": 0,
                            "positionHorizontal": 0,
                            "isImmersive": false
                          },
                          {
                            "id": "w-performance",
                            "type": "performance",
                            "size": "4x2",
                            "useName": false,
                            "showTextMetrics": true,
                            "4x2": {
                              "slot1": { "id": "slot1", "design": "CatDog", "device": "cpu", "gpuIndex": 0, "sensor": { "name": "CPU Core #1", "type": "Load" }, "drive": "C:\\" },
                              "slot2": { "id": "slot2", "design": "WaterLevel", "device": "cpu", "gpuIndex": 0, "sensor": { "name": "CPU Core", "type": "Temperature" }, "drive": "C:\\" },
                              "slot3": { "id": "slot3", "design": "text", "device": "gpu", "gpuIndex": 0, "sensor": { "name": "GPU Core", "type": "Load" }, "drive": "C:\\" },
                              "slot4": { "id": "slot4", "design": "Catapillar", "device": "gpu", "gpuIndex": 0, "sensor": { "name": "GPU Memory Used", "type": "SmallData" }, "drive": "C:\\" },
                              "slot5": { "id": "slot5", "design": "LittleGuy", "device": "memory", "gpuIndex": 0, "sensor": { "name": "Memory", "type": "Load" }, "drive": "C:\\" }
                            },
                            "position": 8,
                            "positionHorizontal": 8,
                            "isImmersive": false
                          },
                          {
                            "id": "w-gallery",
                            "type": "gallery",
                            "size": "4x4",
                            "background": false,
                            "mode": "playlist",
                            "interval": "Every 15 seconds",
                            "random": false,
                            "videoWait": true,
                            "files": [
                              { "id": "f1", "name": "existing-a.jpg", "path": "__SIM_DIR__/existing-a.jpg" },
                              { "id": "f2", "name": "existing-b.jpg", "path": "__SIM_DIR__/existing-b.jpg" },
                              { "id": "f3", "name": "missing.jpg", "path": "__SIM_DIR__/missing.jpg" }
                            ],
                            "position": 16,
                            "positionHorizontal": 16,
                            "isImmersive": false
                          },
                          {
                            "id": "w-media",
                            "type": "media",
                            "size": "4x2",
                            "reactive": false,
                            "theme": "lava",
                            "transparent": false,
                            "livingMedia": true,
                            "albumCoverMode": "static",
                            "controls": true,
                            "kale": { "active": false, "blur": true, "speed": 0.00002 },
                            "position": 24,
                            "positionHorizontal": 24,
                            "isImmersive": false
                          },
                          {
                            "id": "w-snake",
                            "type": "snakeGame",
                            "size": "2x2",
                            "position": 32,
                            "positionHorizontal": 32,
                            "isImmersive": false
                          },
                          {
                            "id": "w-weather",
                            "type": "weather",
                            "size": "4x2",
                            "units": "c",
                            "weatherLocation": { "lat": 48.2082, "long": 16.3738, "display_name": "Vienna, Austria" },
                            "position": 40,
                            "positionHorizontal": 40,
                            "isImmersive": false
                          },
                          {
                            "id": "w-aquarium",
                            "type": "aquarium",
                            "size": "2x2",
                            "position": 48,
                            "positionHorizontal": 48,
                            "isImmersive": false
                          }
                        ]
                      },
                      {
                        "id": "page-2",
                        "type": "page",
                        "widgets": [
                          {
                            "id": "w-whiteboard",
                            "type": "whiteboard",
                            "size": "4x4",
                            "position": 0,
                            "positionHorizontal": 0,
                            "isImmersive": false
                          },
                          {
                            "id": "w-discord",
                            "type": "discord",
                            "size": "2x2",
                            "privacyMode": true,
                            "position": 16,
                            "positionHorizontal": 16,
                            "isImmersive": false
                          }
                        ]
                      }
                    ],
                    "immersiveOnLoad": { "enabled": false, "widgetType": null }
                  }
                }
              },
              "q60": {
                "software": {
                  "activePageId": "qpage-1",
                  "carousel": { "enabled": false, "interval": 15000 },
                  "autoFocus": { "enabled": false, "defaultPageId": "qpage-1", "pages": {} },
                  "background": {
                    "id": "bg-1",
                    "type": "media-background",
                    "gallerySource": "sunset.jpg",
                    "playlistMode": false,
                    "playlistInterval": 5000,
                    "alphaOverlay": "none",
                    "speed": 0
                  },
                  "pages": [
                    {
                      "id": "qpage-1",
                      "front": {
                        "id": "qpage-1",
                        "type": "clock",
                        "timeFormat": "12",
                        "design": "analog",
                        "bounce": true,
                        "theme": { "textColor": "255, 255, 255", "accentColor": "18, 54, 255", "opacity": 1, "overlayOpacity": 0, "overlayColor": "0, 0, 0" }
                      }
                    },
                    {
                      "id": "qpage-2",
                      "front": {
                        "id": "qpage-2",
                        "type": "performance",
                        "design": "None",
                        "sensorRecordTime": 2,
                        "showMulti": true,
                        "slot1": { "sensorId": "cpu-load-main", "gpuIndex": 0, "drive": "C:\\", "device": "cpu", "design": "WaterLevel", "showSensorName": true, "showHardwareName": false },
                        "slot2": { "sensorId": "gpu-load-main", "gpuIndex": 0, "drive": "C:\\", "device": "gpu", "design": "Catapillar", "showSensorName": true, "showHardwareName": false },
                        "slot3": { "sensorId": "pump-in", "gpuIndex": 0, "drive": "C:\\", "device": "q60", "design": "None", "showSensorName": true, "showHardwareName": false },
                        "theme": { "textColor": "255, 255, 255", "accentColor": "200, 50, 50", "opacity": 1, "overlayOpacity": 0, "overlayColor": "0, 0, 0" }
                      }
                    }
                  ]
                },
                "hardware": {
                  "offlineView": "",
                  "mediaFiles": [],
                  "intervalDuration": 0,
                  "text": "",
                  "preview": false,
                  "disableDisplayWithoutSata": false
                }
              }
            }
          ],
          "settings": {
            "general": { "language": "de", "startOnLogin": true, "disableConflictAlerts": false },
            "minihub": { "layout": { "fanPort1": 0, "fanPort2": 0, "rgbPort1": 0, "rgbPort2": 0 } }
          },
          "q60-rotation": "flipped",
          "y70": { "isFirstRun": false, "alwaysOnTop": true, "openOnStart": true }
        }
        """;
}

internal sealed class SimulatedNexus2Detector : INexus2Detector
{
    private int _running = 1;
    private int _autostartPresent = 1;

    public Nexus2DetectionResult Detect() => new(
        Detected: true,
        ImportAvailable: true,
        Version: "2.16.0",
        AutostartTaskPresent: Interlocked.CompareExchange(ref _autostartPresent, 0, 0) == 1,
        Running: Interlocked.CompareExchange(ref _running, 0, 0) == 1,
        InstallLocation: "(simulated)");

    public bool DisableAutostart()
    {
        Interlocked.Exchange(ref _autostartPresent, 0);
        return true;
    }

    public Task<bool> CloseAppAsync()
    {
        Interlocked.Exchange(ref _running, 0);
        return Task.FromResult(true);
    }

    // Stays detected: the simulation exists to exercise the import, and an
    // uninstall that removed it would take the fixture with it.
    public Task<bool> UninstallAsync()
    {
        Interlocked.Exchange(ref _running, 0);
        Interlocked.Exchange(ref _autostartPresent, 0);
        return Task.FromResult(true);
    }
}

/// <summary>Re-parses per call like the real reader, so callers that dispose
/// the result never hand a disposed JsonDocument to a later Preview/Apply.</summary>
internal sealed class SimulatedNexus2ConfigReader : INexus2ConfigReader
{
    public Nexus2ConfigReadResult? Read()
    {
        var dir = SimulatedNexus2.EnsureSourceDir();
        var json = SimulatedNexus2.ConfigJson.Replace("__SIM_DIR__", JsonEncodedText.Encode(dir).ToString());
        return new Nexus2ConfigReadResult(JsonDocument.Parse(json), dir);
    }
}

/// <summary>Mints the y70/q60 panel records the import's device-bound
/// categories target. Only when the surface has no record at all, so a box
/// with a real panel is never touched; factory reset wipes the records and
/// this reseeds them on the next boot.</summary>
internal sealed class SimulatedNexus2Seeder : IHostedService
{
    private readonly PanelDeviceRegistry _panels;
    private readonly IConfigStore _store;

    public SimulatedNexus2Seeder(PanelDeviceRegistry panels, IConfigStore store)
    {
        _panels = panels;
        _store = store;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        SeedIfAbsent(PanelSurfaces.Y70, "Simulated Y70");
        SeedIfAbsent(PanelSurfaces.Q60, "Simulated Q60");
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    private void SeedIfAbsent(string surface, string displayName)
    {
        // Check-then-act is safe here: hosted services start before Kestrel
        // listens, and y70/q60 records are only ever minted via HTTP
        // self-registration, so no real record can appear between the check
        // and the Allocate.
        var exists = _store.Load().PanelDevices.Values
            .Any(d => d.Capabilities?.Surface == surface);
        if (exists)
        {
            return;
        }
        _panels.Allocate(displayName, new PanelDeviceCapabilities { Surface = surface });
    }
}
#endif
