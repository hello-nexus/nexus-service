using System;
using System.Collections.Generic;
using Nexus.Service.Devices.Handlers;

namespace Nexus.Service.Conflicts;

/// <summary>
/// One verified way a conflicting app launches at boot, as the "Disable auto
/// start" action acts on it. Only added for a mechanism confirmed on a real
/// install and observed gone across a reboot - see <see cref="ConflictAutostart"/>.
/// </summary>
public sealed class ConflictAutostartTarget
{
    /// <summary>"runKeyMachine", "runKeyUser", "service" or "scheduledTask".</summary>
    public string Kind { get; init; } = "";

    /// <summary>Service name, for the "service" kind. Unused by the Run kinds, which match on the executable instead.</summary>
    public string Name { get; init; } = "";

    /// <summary>File names (with extension) a Run value must launch to count as this app's. Run kinds only.</summary>
    public string[] ExeNames { get; init; } = Array.Empty<string>();

    /// <summary>
    /// Optional second gate: a path under the console user's Roaming AppData to
    /// the vendor's own settings file. An entry only counts as live when the
    /// vendor also says it will start, and an unreadable file counts as "will
    /// not start" so nothing is offered on a guess.
    /// </summary>
    public string VendorConfigAppDataPath { get; init; } = "";

    /// <summary>Name of the <c>&lt;value name="..."&gt;</c> element in that file whose text must be "true". Requires VendorConfigAppDataPath.</summary>
    public string VendorConfigXmlValue { get; init; } = "";

}

/// <summary>
/// Static catalog of third-party apps that compete with Nexus for hardware
/// control (RGB lighting, fan speeds, peripheral firmware, GPU overlays).
/// The ConflictWatcher scans running processes and surfaces any match in
/// the sidebar warning.
///
/// The watcher dedupes by Id. ProcessNames use the OS-level
/// <see cref="System.Diagnostics.Process.ProcessName"/> convention: no .exe
/// suffix on Windows, just the executable basename. Comparisons are
/// case-insensitive.
/// </summary>
public sealed class ConflictAppDefinition
{
    /// <summary>Stable identifier surfaced over the wire (kebab-case).</summary>
    public string Id { get; init; } = "";

    /// <summary>Human-readable name shown in the warning UI.</summary>
    public string DisplayName { get; init; } = "";

    /// <summary>One of "lighting", "cooling", "peripherals", "monitoring" - drives the UI hint.</summary>
    public string Category { get; init; } = "";

    /// <summary>Process names to match against <c>Process.GetProcesses().ProcessName</c>.</summary>
    public string[] ProcessNames { get; init; } = Array.Empty<string>();

    /// <summary>Windows service names to stop, in order, when ending this app - for apps whose background service holds the hardware. Empty for most.</summary>
    public string[] WindowsServiceNames { get; init; } = Array.Empty<string>();

    /// <summary>OpenRGB vendor strings (substring, case-insensitive) whose devices this app drives. Empty for apps that map to no OpenRGB device.</summary>
    public string[] Vendors { get; init; } = Array.Empty<string>();

    /// <summary>True for universal RGB apps that drive every OpenRGB device regardless of vendor.</summary>
    public bool ClaimsAllRgb { get; init; }

    /// <summary>Verified boot-launch mechanisms. Empty - the default for most of the catalog - means the UI offers no "Disable auto start" action for this app at all.</summary>
    public IReadOnlyList<ConflictAutostartTarget> Autostart { get; init; } = Array.Empty<ConflictAutostartTarget>();
}

public static class ConflictAppCatalog
{
    /// <summary>
    /// Apps that contend with Nexus for hardware control when run alongside it.
    /// ProcessNames are the Windows
    /// <see cref="System.Diagnostics.Process.ProcessName"/> form (basename
    /// without the .exe extension).
    /// </summary>
    public static readonly IReadOnlyList<ConflictAppDefinition> All = new ConflictAppDefinition[]
    {
        // ── HYTE Nexus 2 (legacy) ──────────────────────────────────────────
        new()
        {
            Id = "hyte-nexus-2",
            ClaimsAllRgb = true,
            DisplayName = "HYTE Nexus 2",
            Category = "lighting",
            ProcessNames = new[] { "HYTE Nexus", "HYTE.Nexus.Service" },
            // Nexus 2 arms itself with an at-logon scheduled task, not a Run
            // value - which is why a Run-key sweep finds nothing while the app
            // starts every logon. Same task the Nexus 2 migration path deletes
            // (Nexus2Detector.DisableAutostart), so both surfaces agree.
            Autostart = new ConflictAutostartTarget[]
            {
                new() { Kind = ConflictAutostart.KindScheduledTask, Name = "HYTE Nexus" },
            },
            // HYTEIO is the HYTE kernel IO driver the service holds hardware
            // through; it survives an uninstall, so it must be stopped
            // alongside the two processes for the conflict to fully clear.
            WindowsServiceNames = new[] { "HYTEIO" },
        },

        // ── NZXT ────────────────────────────────────────────────────────────
        new()
        {
            Id = "nzxt-cam",
            Vendors = new[] { "NZXT" },
            DisplayName = "NZXT CAM",
            Category = "lighting",
            ProcessNames = new[] { "NZXT CAM", "CAM", "NZXT CAM Beta", "NZXT CAM Service", "NZXTCAM" },
            // Stopped alongside the processes on End task. CAMService is NZXT's
            // own; Windows' unrelated "camsvc" (svchost -k osprivacy) must never
            // appear here.
            WindowsServiceNames = new[] { "CAMService" },
            // Two mechanisms. The Automatic CAMService is CAM's background half;
            // the per-user Run value ("NZXT.CAM" -> NZXT CAM.exe --startup) is
            // what brings the UI up, and CAM writes it for itself once run, so a
            // box can gain it at any time. Listing only the service read a box
            // carrying the Run value as "nothing armed".
            Autostart = new ConflictAutostartTarget[]
            {
                new() { Kind = ConflictAutostart.KindService, Name = "CAMService" },
                new() { Kind = ConflictAutostart.KindRunKeyUser, ExeNames = new[] { "NZXT CAM.exe" } },
            },
        },
        new()
        {
            Id = "nzxt-kraken",
            DisplayName = "NZXT Kraken",
            Category = "cooling",
            ProcessNames = new[] { "NZXT Kraken" },
        },

        // ── SignalRGB / OpenRGB ────────────────────────────────────────────
        new()
        {
            Id = "signalrgb",
            ClaimsAllRgb = true,
            DisplayName = "SignalRGB",
            Category = "lighting",
            ProcessNames = new[] { "SignalRgb", "SignalRgbLauncher", "SignalRgbService", "SignalRgb.Service" },
            // Stopped alongside the processes on End task; it restarts them.
            WindowsServiceNames = new[] { "SignalRgb.Service" },
            // Two mechanisms, and which one a box carries varies: the Y70 runs
            // the Automatic service with no Run value anywhere, while T1 has the
            // service Manual and a per-user Run value ("SignalRgb" ->
            // SignalRgbLauncher.exe --silent) that the app re-registers for
            // itself when launched. Listing only the service read T1 as "nothing
            // armed" while SignalRGB started every boot, so both are listed and
            // whichever is live gets reported.
            Autostart = new ConflictAutostartTarget[]
            {
                new() { Kind = ConflictAutostart.KindService, Name = "SignalRgb.Service" },
                new() { Kind = ConflictAutostart.KindRunKeyUser, ExeNames = new[] { "SignalRgbLauncher.exe", "SignalRgb.exe" } },
            },
        },
        new()
        {
            Id = "openrgb",
            ClaimsAllRgb = true,
            DisplayName = "OpenRGB",
            Category = "lighting",
            // Nexus bundles its own headless OpenRGB subprocess (see nexus-rgb). A
            // user-launched OpenRGB.exe with its own GUI will conflict, but our
            // bundled child process runs from inside our install dir, so the
            // watcher filters it out at scan time (see ConflictWatcher).
            ProcessNames = new[] { "OpenRGB" },
        },

        // ── ASUS ───────────────────────────────────────────────────────────
        new()
        {
            Id = "asus-ai-suite-3",
            DisplayName = "ASUS AI Suite 3",
            Category = "cooling",
            // AISuite3 = real binary. AlSuite3 is a propagated typo from the
            // upstream Nexus registry kept as a defensive alias.
            ProcessNames = new[] { "AISuite3", "AlSuite3" },
        },
        new()
        {
            Id = "armoury-crate",
            Vendors = new[] { "ASUS" },
            DisplayName = "ASUS Armoury Crate",
            Category = "lighting",
            ProcessNames = new[] { "ArmouryCrate", "ArmouryCrate.Service", "ArmouryCrate.UserSessionHelper", "ArmourySocketServer", "Armoury Crate" },
        },
        new()
        {
            Id = "asus-lighting-service",
            Vendors = new[] { "ASUS" },
            DisplayName = "ASUS Lighting Service",
            Category = "lighting",
            ProcessNames = new[] { "LightingService" },
        },
        new()
        {
            Id = "aura-sync",
            Vendors = new[] { "ASUS" },
            DisplayName = "ASUS Aura Sync",
            Category = "lighting",
            ProcessNames = new[] { "AuraSync", "AsusAura" },
        },
        new()
        {
            Id = "asus-fan-xpert",
            DisplayName = "ASUS Fan Xpert",
            Category = "cooling",
            ProcessNames = new[] { "FanXpert" },
        },

        // ── ASRock ─────────────────────────────────────────────────────────
        new()
        {
            Id = "asrock-rgb-sync",
            Vendors = new[] { "ASRock" },
            DisplayName = "ASRock RGB Sync",
            Category = "lighting",
            ProcessNames = new[] { "ASRRGBLED" },
        },
        new()
        {
            Id = "asrock-polychrome",
            Vendors = new[] { "ASRock" },
            DisplayName = "ASRock Polychrome RGB",
            Category = "lighting",
            ProcessNames = new[] { "AsrPolychromeRGB" },
        },

        // ── MSI ────────────────────────────────────────────────────────────
        new()
        {
            Id = "msi-afterburner",
            DisplayName = "MSI Afterburner",
            Category = "monitoring",
            ProcessNames = new[] { "MSIAfterburner" },
        },
        new()
        {
            Id = "msi-mystic-light",
            Vendors = new[] { "MSI" },
            DisplayName = "MSI Mystic Light",
            Category = "lighting",
            ProcessNames = new[] { "Mystic_Light", "MysticLight", "MysticLight_x64" },
        },
        new()
        {
            Id = "msi-center",
            Vendors = new[] { "MSI" },
            DisplayName = "MSI Center",
            Category = "lighting",
            ProcessNames = new[] { "MSI.CentralServer", "MSI Center" },
        },
        new()
        {
            Id = "msi-gaming-center",
            Vendors = new[] { "MSI" },
            DisplayName = "MSI Gaming Center",
            Category = "lighting",
            ProcessNames = new[] { "GCC" },
        },
        new()
        {
            Id = "msi-control-center",
            DisplayName = "MSI Control Center",
            Category = "monitoring",
            ProcessNames = new[] { "ControlCenter" },
        },
        new()
        {
            Id = "msi-led-keeper",
            Vendors = new[] { "MSI" },
            DisplayName = "MSI LED Keeper",
            Category = "lighting",
            ProcessNames = new[] { "LedKeeper", "LEDKeeper2" },
        },
        new()
        {
            Id = "msi-companion",
            DisplayName = "MSI Companion",
            Category = "monitoring",
            ProcessNames = new[] { "MSI_Companion_Service" },
        },
        new()
        {
            Id = "msi-game-bar-tool",
            DisplayName = "MSI Game Bar Tool",
            Category = "monitoring",
            ProcessNames = new[] { "MSI_GamebarTool" },
        },
        new()
        {
            Id = "msi-super-charger",
            DisplayName = "MSI Super Charger",
            Category = "monitoring",
            ProcessNames = new[] { "MSI_Super_Charger_Service" },
        },

        // ── Gigabyte ───────────────────────────────────────────────────────
        new()
        {
            Id = "gigabyte-rgb-fusion",
            Vendors = new[] { "Gigabyte" },
            DisplayName = "Gigabyte RGB Fusion",
            Category = "lighting",
            ProcessNames = new[] { "RGBFusion", "RGBFusion2.0", "RGB Fusion" },
        },
        new()
        {
            Id = "gigabyte-aorus",
            DisplayName = "Gigabyte AORUS Engine",
            Category = "monitoring",
            ProcessNames = new[] { "AORUS" },
        },
        new()
        {
            Id = "gigabyte-smart-fan",
            DisplayName = "Gigabyte Smart Fan",
            Category = "cooling",
            ProcessNames = new[] { "SmartFan" },
        },

        // ── Corsair ────────────────────────────────────────────────────────
        new()
        {
            Id = "icue",
            Vendors = new[] { "Corsair" },
            DisplayName = "Corsair iCUE",
            Category = "lighting",
            ProcessNames = new[]
            {
                "iCUE", "iCUE Launcher", "iCUELauncher", "iCUEDevicePluginHost",
                "Corsair.Service", "Corsair.Service.CpuldRemote64", "Corsair.Service.DisplayAdapter",
                "CorsairDeviceControlService", "CueLLAccessService", "CorsairService",
            },
            WindowsServiceNames = new[] { "CorsairDeviceListerService" },
            // The Run value alone is NOT evidence iCUE starts: "iCUE Launcher.exe
            // --autorun" runs either way and then honours iCUE's own setting.
            // Two boxes carrying an identical enabled Run value differed only in
            // config.cuecfg's StartOnStartup, and only the true one came up after
            // a reboot - hence the vendor flag as a second gate.
            Autostart = new ConflictAutostartTarget[]
            {
                new()
                {
                    Kind = ConflictAutostart.KindRunKeyMachine,
                    ExeNames = new[] { "iCUE Launcher.exe", "iCUE.exe" },
                    VendorConfigAppDataPath = @"Corsair\CUE5\config.cuecfg",
                    VendorConfigXmlValue = "StartOnStartup",
                },
                new() { Kind = ConflictAutostart.KindService, Name = "CorsairDeviceListerService" },
            },
        },
        new()
        {
            Id = "corsair-link4",
            DisplayName = "Corsair Link 4",
            Category = "cooling",
            ProcessNames = new[] { "CorsairLink4" },
        },

        // ── Razer ──────────────────────────────────────────────────────────
        new()
        {
            Id = "razer-synapse",
            Vendors = new[] { "Razer" },
            DisplayName = "Razer Synapse",
            Category = "peripherals",
            // RazerAppEngine is Synapse 4's only process - a 4.x install carries
            // none of the Synapse 3 names, so without it Synapse 4 is invisible
            // to the watcher.
            ProcessNames = new[] { "RazerAppEngine", "Razer Synapse 3", "RzSynapse", "Razer Synapse Service", "RazerCentralService", "Razer Central" },
            // Synapse 4 autostarts from a per-user Run value ("RazerAppEngine"
            // -> RazerAppEngine.exe --autoStart=1), read out of the console
            // user's hive from the LocalSystem service.
            Autostart = new ConflictAutostartTarget[]
            {
                new() { Kind = ConflictAutostart.KindRunKeyUser, ExeNames = new[] { "RazerAppEngine.exe" } },
            },
        },
        new()
        {
            Id = "razer-chroma-sdk",
            Vendors = new[] { "Razer" },
            DisplayName = "Razer Chroma SDK",
            Category = "lighting",
            ProcessNames = new[] { "Razer Chroma SDK Service" },
        },

        // ── Logitech ───────────────────────────────────────────────────────
        new()
        {
            Id = "logitech-ghub",
            Vendors = new[] { "Logitech" },
            DisplayName = "Logitech G HUB",
            Category = "peripherals",
            ProcessNames = new[] { "lghub", "lghub_agent", "lghub_system_tray", "logi_overlay" },
        },

        // ── Lian Li ────────────────────────────────────────────────────────
        new()
        {
            Id = "lian-li-l-connect",
            Vendors = new[] { "Lian Li" },
            DisplayName = "Lian Li L-Connect",
            Category = "lighting",
            ProcessNames = new[] { "L-Connect 3", "L-Connect", "LConnect3", "LConnect" },
            // Watcher first so it cannot restart the main service.
            WindowsServiceNames = new[] { "LConnectServiceWatcher", "LConnectService" },
        },

        // ── Tryx ───────────────────────────────────────────────────────────
        new()
        {
            // Tryx Panorama control app (Electron, C:\Program Files\KANALI);
            // claims the panel's USB handle Nexus drives directly.
            Id = "tryx-kanali",
            DisplayName = "Tryx Kanali",
            Category = "cooling",
            ProcessNames = new[] { "Kanali" },
        },

        // ── ZMatrices ──────────────────────────────────────────────────────
        new()
        {
            // Cooler LCD app (Electron, C:\Program Files\ZMatrices); its sender process
            // holds the panel's WinUSB handle Nexus drives directly.
            Id = "zmatrices",
            DisplayName = "ZMatrices",
            Category = "cooling",
            ProcessNames = new[] { "ZMatrices", "zmUsbSendJpg" },
        },

        // ── Elgato ─────────────────────────────────────────────────────────
        new()
        {
            // Elgato's own Stream Deck app can hold the same HID handle
            // concurrently as Nexus (see StreamDeckHandler.GetWarning), so
            // both apps painting the deck is a visual fight, not a
            // connection failure. Id references the same constant
            // StreamDeckRoutes surfaces as conflictAppId, so the two never
            // drift apart.
            Id = StreamDeckHandler.ElgatoConflictAppId,
            Vendors = new[] { "Elgato" },
            DisplayName = "Elgato Stream Deck",
            Category = "peripherals",
            ProcessNames = new[] { "StreamDeck" },
        },

        // ── Other peripheral / lighting vendors ────────────────────────────
        new()
        {
            Id = "glorious-core",
            Vendors = new[] { "Glorious" },
            DisplayName = "Glorious Core",
            Category = "peripherals",
            ProcessNames = new[] { "Glorious Core" },
        },
        new()
        {
            Id = "fnatic-op",
            DisplayName = "Fnatic OP",
            Category = "peripherals",
            ProcessNames = new[] { "Fnatic OP" },
        },
        new()
        {
            Id = "steelseries-engine",
            Vendors = new[] { "SteelSeries" },
            DisplayName = "SteelSeries Engine",
            Category = "peripherals",
            ProcessNames = new[] { "SteelSeriesEngine" },
        },
        new()
        {
            Id = "steelseries-gg",
            Vendors = new[] { "SteelSeries" },
            DisplayName = "SteelSeries GG",
            Category = "peripherals",
            ProcessNames = new[] { "SteelSeriesGG", "SteelSeriesGGClient" },
        },
        new()
        {
            Id = "steelseries-prism",
            Vendors = new[] { "SteelSeries" },
            DisplayName = "SteelSeries Prism",
            Category = "lighting",
            ProcessNames = new[] { "SteelSeriesPrism" },
        },
        new()
        {
            Id = "roccat-swarm",
            Vendors = new[] { "ROCCAT" },
            DisplayName = "ROCCAT Swarm",
            Category = "peripherals",
            ProcessNames = new[] { "ROCCAT_Swarm", "ROCCAT_Swarm_Monitor", "ROCCAT_dev_service" },
        },
        new()
        {
            Id = "xpg-prime",
            Vendors = new[] { "XPG", "ADATA" },
            DisplayName = "XPG Prime",
            Category = "lighting",
            ProcessNames = new[] { "XPG-Prime" },
        },

        // ── EVGA ───────────────────────────────────────────────────────────
        new()
        {
            Id = "evga-precision-x1",
            Vendors = new[] { "EVGA" },
            DisplayName = "EVGA Precision X1",
            Category = "monitoring",
            ProcessNames = new[] { "PrecisionX_x64" },
        },
        new()
        {
            Id = "evga-precision-x-server",
            DisplayName = "EVGA Precision X Server",
            Category = "monitoring",
            ProcessNames = new[] { "EVGAPrecisionXServer" },
        },
        new()
        {
            Id = "evga-aio",
            DisplayName = "EVGA AIO Control",
            Category = "cooling",
            ProcessNames = new[] { "EVGAAIO" },
        },

        // ── Thermaltake ────────────────────────────────────────────────────
        new()
        {
            Id = "thermaltake-itake",
            Vendors = new[] { "Thermaltake" },
            DisplayName = "Thermaltake iTAKE Engine",
            Category = "lighting",
            ProcessNames = new[] { "TT iTAKE Engine" },
        },
        new()
        {
            Id = "thermaltake-rgb-plus",
            Vendors = new[] { "Thermaltake" },
            DisplayName = "Thermaltake RGB Plus",
            Category = "lighting",
            // TTRGBPlusGUI = real binary (the upstream Nexus registry has a
            // lowercase-L typo "TTRGBPlusGUl" we keep as a defensive alias).
            ProcessNames = new[] { "TTRGBPlus", "TTRGBPlusGUI", "TTRGBPlusGUl" },
        },
        new()
        {
            Id = "thermaltake-dps-g",
            DisplayName = "Thermaltake DPS G",
            Category = "monitoring",
            ProcessNames = new[] { "TT DPS G" },
        },

        // ── Cooler Master ──────────────────────────────────────────────────
        new()
        {
            Id = "cooler-master-plus",
            Vendors = new[] { "Cooler Master" },
            DisplayName = "Cooler Master MasterPlus+",
            Category = "lighting",
            ProcessNames = new[] { "CoolerMasterPlus" },
        },

        // ── Fan / temp control ─────────────────────────────────────────────
        new()
        {
            Id = "fan-control",
            DisplayName = "FanControl",
            Category = "cooling",
            ProcessNames = new[] { "FanControl" },
        },
        new()
        {
            Id = "speedfan",
            DisplayName = "SpeedFan",
            Category = "cooling",
            ProcessNames = new[] { "speedfan" },
        },
        new()
        {
            Id = "argus-monitor",
            DisplayName = "Argus Monitor",
            Category = "monitoring",
            ProcessNames = new[] { "ArgusMonitor" },
        },
        new()
        {
            Id = "notebook-fan-control",
            DisplayName = "NoteBook Fan Control",
            Category = "cooling",
            ProcessNames = new[] { "NoteBookFanControl" },
        },
        new()
        {
            Id = "fanctrl",
            DisplayName = "FanCtrl",
            Category = "cooling",
            ProcessNames = new[] { "FanCtrl" },
        },
        new()
        {
            Id = "argb-fan-master",
            DisplayName = "Argb Fan Master",
            Category = "lighting",
            ProcessNames = new[] { "ArgbFanMaster" },
        },
    };
}
