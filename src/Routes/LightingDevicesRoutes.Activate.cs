using System.Collections.Generic;
using Nexus.Service.Devices;
using Nexus.Service.Models;
using Nexus.Service.Models.Devices;

namespace Nexus.Service.Routes;

public static partial class DevicesRoutes
{
    /// <summary>Applies a layout preset to the live state and makes it active.
    /// False when no preset carries that id. Shared by the activate route and
    /// <c>AppPresetSwitcher</c>, which activates on foreground changes.</summary>
    internal static bool ActivateLayoutPreset(
        string id,
        Nexus.Service.Persistence.IConfigStore store,
        Nexus.Service.Sockets.MultiplexHub hub,
        ILightingDeviceProvider lightingProvider,
        Nexus.Service.Lighting.ILightingProvider lighting,
        Nexus.Service.Lighting.Rgb.RgbBridge? bridge,
        Nexus.Service.Lighting.Smart.SmartLightProvider smart,
        Nexus.Service.Lighting.Engine.LightingEngine engine)
    {
        var s = store.Load();
        var preset = s.Lighting.LayoutPresets.Find(p => p.Id == id);
        if (preset is null)
        {
            return false;
        }

        var layouts = new Dictionary<string, Nexus.Service.Persistence.DeviceLayout>(preset.Layouts);
        var disabled = preset.DisabledDevices is null ? null : new List<string>(preset.DisabledDevices);
        var uncontrolled = preset.UncontrolledDevices is null ? null : new List<string>(preset.UncontrolledDevices);
        var look = preset.Look;
        var staticLooks = preset.StaticDeviceLooks is null ? null : DeepCopyStaticLooks(preset.StaticDeviceLooks);
        var devicePrefs = preset.DevicePrefs is null ? null : DeepCopyDevicePrefs(preset.DevicePrefs);
        var sceneView = preset.SceneView is null ? null : CopySceneView(preset.SceneView);
        // Ids the preset re-enables. Smart lights re-push their static colour
        // on re-enable, mirroring POST /devices/lighting-devices/controlled.
        var reControlled = uncontrolled is null
            ? new List<string>()
            : s.Devices.UncontrolledLightingDevices.FindAll(x => !uncontrolled.Contains(x));
        store.Update(settings =>
        {
            settings.Lighting.DeviceLayouts.Clear();
            foreach (var kv in layouts)
            {
                settings.Lighting.DeviceLayouts[kv.Key] = kv.Value;
            }
            settings.Lighting.ActiveLayoutPresetId = id;
            if (disabled is not null)
            {
                settings.Devices.DisabledLightingDevices = new List<string>(disabled);
            }
            if (uncontrolled is not null)
            {
                // Replaced, not mutated: the frame writers read this list
                // lock-free and must never observe a torn state.
                settings.Devices.UncontrolledLightingDevices = new List<string>(uncontrolled);
            }
            if (look is not null)
            {
                Nexus.Service.Lighting.LightingPresetLooks.Apply(settings.Lighting, look);
            }
            if (staticLooks is not null)
            {
                // Replaced wholesale: StaticDeviceEffectTracker re-hydrates
                // off the store's OnChanged, so writing this is what repaints
                // the devices - no separate replay.
                settings.Lighting.StaticDeviceLooks = staticLooks;
            }
            if (devicePrefs is not null)
            {
                settings.Devices.LightingDevicePrefs = devicePrefs;
            }
            if (sceneView is not null)
            {
                // LightingSceneService rebuilds the engine projection off the store change.
                settings.Lighting.SceneView = sceneView;
            }
        });
        if (uncontrolled is not null)
        {
            bridge?.RequestTopologyRefresh();
            foreach (var reEnabled in reControlled)
            {
                smart.RestoreStatic(reEnabled);
            }
        }
        MirrorLayoutsToEngine(layouts, lightingProvider, engine);
        if (look is not null)
        {
            EngageLook(look, store, lighting);
        }
        Nexus.Service.Sockets.PanelTopics.BroadcastLighting(hub);
        return true;
    }
}
