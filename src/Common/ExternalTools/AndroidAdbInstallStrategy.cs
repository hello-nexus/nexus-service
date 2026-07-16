using System;
using System.Collections.Concurrent;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Nexus.Service.Panel;
using Nexus.Service.Platform;

namespace Nexus.Service.Common.ExternalTools;

/// <summary>Delegate type for the adb install command, injectable in tests.</summary>
internal delegate (bool success, string output) AdbInstallRunner(string adbPath, string serial, string apkPath);

/// <summary>Delegate type for the adb uninstall command, injectable in tests.</summary>
internal delegate (bool success, string output) AdbUninstallRunner(string adbPath, string serial, string package);

/// <summary>
/// Installs an APK onto an adb-connected Android device via <c>adb install -r</c>.
/// Never downgrades: skips if the installed versionCode is already >= the published one.
/// The actual install is gated by <see cref="EnableApkInstall"/>; when false the device
/// target is still registered and queryable, but no APK is pushed.
/// </summary>
public sealed class AndroidAdbInstallStrategy : IToolInstallStrategy
{
    /// <summary>
    /// Set to true only after bench confirming adb install works on the target panel.
    /// No APK is pushed to any field panel while this is false.
    /// </summary>
    private const bool EnableApkInstall = false;

    private readonly IAdbDeviceRegistry _registry;
    private readonly bool _installEnabled;
    private readonly string? _adbPath;
    private readonly AdbInstallRunner _runner;
    private readonly ConcurrentDictionary<string, byte> _installed = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, byte> _failed = new(StringComparer.Ordinal);

    private static readonly Regex VersionCodeRegex = AdbHelpers.VersionCodeRegex;

    public ToolTarget Target => ToolTarget.AndroidAdb;

    public AndroidAdbInstallStrategy(IAdbDeviceRegistry registry)
        : this(registry, installEnabled: EnableApkInstall) { }

    internal AndroidAdbInstallStrategy(IAdbDeviceRegistry registry, bool installEnabled, AdbInstallRunner? runner = null, string? adbPath = null)
    {
        _registry = registry;
        _installEnabled = installEnabled;
        _adbPath = adbPath;
        _runner = runner ?? RunAdbInstallDefault;
    }

    public async Task LaunchAsync(ExternalToolSpec spec, IToolResolver resolver, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(spec.Package))
        {
            ServiceLog.Warn($"[tools:adb] {spec.ToolId}: no package name in spec; skipping");
            return;
        }

        var device = _registry.TryGet(spec.Package);
        if (device is null)
        {
            ServiceLog.Info($"[tools:adb] {spec.ToolId}: no device registered for package {spec.Package}; skipping");
            return;
        }

        if (!_installEnabled)
        {
            ServiceLog.Info($"[tools:adb] {spec.ToolId}: install gate off; skipping");
            return;
        }

        if (_installed.ContainsKey(spec.ToolId))
        {
            return;
        }

        int installedVersionCode;
        try
        {
            var dumpsys = await device.ShellAsync($"dumpsys package {spec.Package}", ct);
            var match = VersionCodeRegex.Match(dumpsys);
            installedVersionCode = match.Success ? int.Parse(match.Groups[1].Value) : -1;
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            ServiceLog.Warn($"[tools:adb] {spec.ToolId}: dumpsys package failed: {ex.GetType().Name}: {ex.Message}");
            return;
        }

        // Read published versionCode from the manifest before downloading the payload;
        // skip the 25 MB download when the installed code is already current.
        var latest = await resolver.GetLatestAsync(spec, ct);
        if (latest?.VersionCode is int published && installedVersionCode >= published)
        {
            ServiceLog.Info($"[tools:adb] {spec.ToolId}: package {spec.Package} already at versionCode {installedVersionCode}; skipping");
            _installed[spec.ToolId] = 1;
            return;
        }

        var apkPath = await resolver.ResolveAsync(spec, ct);
        if (apkPath is null)
        {
            ServiceLog.Warn($"[tools:adb] {spec.ToolId}: resolver returned null; cannot install");
            _failed[spec.ToolId] = 1;
            return;
        }

        var resolvedAdb = _adbPath ?? AdbLocator.ResolveAdbPath();
        if (resolvedAdb is null)
        {
            ServiceLog.Warn($"[tools:adb] {spec.ToolId}: adb not found; cannot install");
            _failed[spec.ToolId] = 1;
            return;
        }

        device.InstallInProgress = true;
        bool success;
        string output;
        bool signatureRecovery;
        try
        {
            (success, output, signatureRecovery) =
                AdbHelpers.InstallWithSignatureRecovery(resolvedAdb, device.Serial, apkPath, spec.Package!, _runner);
        }
        finally
        {
            device.InstallInProgress = false;
        }

        if (signatureRecovery)
        {
            ServiceLog.Warn($"[tools:adb] {spec.ToolId}: cert conflict on {device.Serial}; uninstalled {spec.Package} and retried");
        }

        if (success)
        {
            ServiceLog.Info($"[tools:adb] {spec.ToolId}: installed {spec.Package} on {device.Serial}");
            _failed.TryRemove(spec.ToolId, out _);
            _installed[spec.ToolId] = 1;
        }
        else
        {
            ServiceLog.Warn($"[tools:adb] {spec.ToolId}: install failed on {device.Serial}: {output}");
            _failed[spec.ToolId] = 1;
        }
    }

    public ToolStatus GetStatus(string toolId)
    {
        if (_installed.ContainsKey(toolId)) return ToolStatus.Running;
        if (_failed.ContainsKey(toolId)) return ToolStatus.Failed;
        return ToolStatus.NotRunning;
    }

    /// <summary>An installed APK is not a host process; nothing to stop.</summary>
    public bool Terminate(string toolId) => false;

    public void TerminateAll() { }

    private static (bool success, string output) RunAdbInstallDefault(string adbPath, string serial, string apkPath)
        => AdbHelpers.RunAdbInstall(adbPath, serial, apkPath);
}
