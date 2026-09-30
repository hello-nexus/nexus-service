using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using Nexus.Service.Devices.Firmware;
using Nexus.Service.Lifecycle;
using Nexus.Service.Models.Update;
using Nexus.Service.Persistence;
using Nexus.Service.Sockets;
#if WINDOWS
using Nexus.Service.Helper;
using Nexus.Service.Helper.Domains;
#endif

namespace Nexus.Service.Update;

/// <summary>
/// Singleton update engine. Implements IHostedService so it polls on a
/// background timer. Holds mutable status and progress DTOs the routes read.
///
/// Key invariants:
/// - Never blocks startup. All polling + installs are fire-and-forget or
///   background tasks. Every poll failure is swallowed into lastCheckError.
/// - One install at a time (gate via Interlocked + CancellationTokenSource).
/// - UpdateMode "notify": detect only; no background download.
/// - UpdateMode "download": stage a download+verify; installer not launched
///   until the user triggers POST /update/start.
/// - UpdateMode "always": stage a download+verify on detection; apply on the
///   next restart via ApplyPendingOnStartup (never launches mid-session).
/// </summary>
public sealed class UpdateService : BackgroundService
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromHours(4);

    private readonly IUpdateSource _source;
    private readonly UpdateDownloader _downloader;
    private readonly IConfigStore _store;
    private readonly FirmwareFlasher _flasher;
    private readonly MultiplexHub _hub;
#if WINDOWS
    private readonly HelperRegistry _helperRegistry;
#endif

    // Status DTO - read by routes, written only by this service.
    private volatile UpdateStatusResponse _status = new() { CurrentVersion = BuildInfo.Version };
    // Progress DTO - read by routes during an active install.
    private volatile UpdateProgressResponse _progress = new();

    // Set when ApplyPendingOnStartup confirms the version advanced. Returned by
    // GET /update/status for 60 seconds, then cleared. Not cleared on read, so
    // concurrent readers (the sidebar poll and the what's-new auto-opener) all
    // see it; clearing on first read let the sidebar consume it and the
    // auto-opener miss the post-update what's-new view.
    // Accessed only via Volatile.Read/Write + Interlocked; no `volatile` keyword
    // because passing a volatile field by ref to Interlocked is CS0420.
    private string _justUpdatedTo = "";
    private long _justUpdatedToSetAtTicks;
    private const long JustUpdatedToTimeoutTicks = 60L * TimeSpan.TicksPerSecond;

    // Gate: 0 = idle, 1 = in progress.
    private int _installing;
    private CancellationTokenSource? _installCts;

    // A background auto-stage (download/always) holds _installing while it
    // downloads. A user trigger arriving in that window sets this so the stage
    // launches the installer when it finishes, instead of being rejected.
    private volatile bool _launchAfterStage;
    private volatile bool _launchAfterStageReopen;

    private volatile UpdateManifest? _latestManifest;
    private volatile bool _updateReady;

    // Set once a check reaches the source (network/DNS up). Gates the cold-boot
    // short-retry so a transient boot-time DNS failure isn't stranded until the
    // next PollInterval.
    private volatile bool _hadSuccessfulCheck;
    private volatile string? _stagedInstallerPath;
    // Version the staged installer is for. A newer manifest supersedes it, so the
    // auto-stage guard re-stages instead of leaving the queued install on the old
    // version.
    private volatile string? _stagedVersion;

    // Last (updateAvailable, updateReady) pushed over the WS. The status-changed
    // broadcast fires only on the rising edge of either, keeping the 4h poll off
    // the wire when the state is unchanged.
    private bool _broadcastUpdateAvailable;
    private bool _broadcastUpdateReady;
    private readonly object _broadcastLock = new();

    public UpdateService(
        IUpdateSource source,
        UpdateDownloader downloader,
        IConfigStore store,
        FirmwareFlasher flasher,
        MultiplexHub hub
#if WINDOWS
        , HelperRegistry helperRegistry
#endif
        )
    {
        _source = source;
        _downloader = downloader;
        _store = store;
        _flasher = flasher;
        _hub = hub;
#if WINDOWS
        _helperRegistry = helperRegistry;
#endif
    }

    /// <summary>Current status snapshot for GET /update/status.</summary>
    public UpdateStatusResponse Status
    {
        get
        {
            var snap = _status;
            var justUpdated = Volatile.Read(ref _justUpdatedTo);
            if (justUpdated == "")
            {
                return snap;
            }

            var elapsed = DateTime.UtcNow.Ticks - Volatile.Read(ref _justUpdatedToSetAtTicks);
            if (elapsed < JustUpdatedToTimeoutTicks)
            {
                return SnapWithJustUpdatedTo(snap, justUpdated);
            }

            // Window elapsed: clear so later reads skip the timeout check. Guard
            // against clobbering a newer value set between the read and here.
            Interlocked.CompareExchange(ref _justUpdatedTo, "", justUpdated);
            return snap;
        }
    }

    private static UpdateStatusResponse SnapWithJustUpdatedTo(UpdateStatusResponse snap, string justUpdated)
    {
        return new UpdateStatusResponse
        {
            CurrentVersion = snap.CurrentVersion,
            LatestVersion = snap.LatestVersion,
            UpdateAvailable = snap.UpdateAvailable,
            Channel = snap.Channel,
            UpdateMode = snap.UpdateMode,
            ReleaseNotes = snap.ReleaseNotes,
            LastCheckedUnix = snap.LastCheckedUnix,
            LastCheckError = snap.LastCheckError,
            State = snap.State,
            UpdateReady = snap.UpdateReady,
            JustUpdatedTo = justUpdated,
            PublishedAtUnix = snap.PublishedAtUnix,
            DownloadUrl = snap.DownloadUrl,
            CanAutoInstall = snap.CanAutoInstall,
        };
    }

    /// <summary>Current progress snapshot for GET /update/progress.</summary>
    public UpdateProgressResponse Progress => _progress;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
#if WINDOWS
        // Clean any stale OTA installer tasks from a prior interrupted install.
        try { UpdateInstaller.CleanOrphanedTasks(); } catch { }
#endif

        // Align UpdateChannel with the running build when the version changed.
        SyncChannelToVersion();

        // Apply or diagnose a staged install marker from a prior run.
        ApplyPendingOnStartup(stoppingToken);

        // ApplyPendingOnStartup no longer self-stops; fall through to the poll
        // loop, which the installer's net stop cancels when an install proceeds.
        if (stoppingToken.IsCancellationRequested)
        {
            return;
        }

        // Housekeeping above still runs unconditionally: a machine rebuilt without
        // a credential must still apply or clear a marker an official build staged.
        // Only the poll loop is ours.
        if (!Common.ClientCredential.IsOfficial)
        {
            return;
        }

        // Beat once at startup, then every PollInterval. PeriodicTimer drops
        // drift if a check runs long.
        using var timer = new PeriodicTimer(PollInterval);
        do
        {
            try
            {
                if (!Nexus.Service.FocusModes.FocusNetworkGate.IsHeld)
                    await PollAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[update] poll iteration failed: {ex.GetType().Name}: {ex.Message}");
            }

            if (!_hadSuccessfulCheck
                && !await RetryUntilFirstCheckAsync(stoppingToken).ConfigureAwait(false))
            {
                return;
            }
        }
        while (await WaitAsync(timer, stoppingToken).ConfigureAwait(false));
    }

    /// <summary>
    /// The startup check can fire before the network/DNS stack is up on a cold
    /// boot, failing to resolve api.github.com. Retry on a short capped backoff
    /// until a check reaches the source, so a transient boot failure isn't
    /// stranded until the next PollInterval. Returns false if cancelled.
    /// </summary>
    private async Task<bool> RetryUntilFirstCheckAsync(CancellationToken ct)
    {
        // Cold-boot DNS comes up within seconds; cap the fast-retry window (~10
        // min of 15s->120s backoff) so a box that is genuinely offline falls
        // back to the normal 4h cadence instead of polling (and logging) forever.
        const int maxAttempts = 8;
        var delaySeconds = 15;
        for (var attempt = 0; attempt < maxAttempts && !_hadSuccessfulCheck && !ct.IsCancellationRequested; attempt++)
        {
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(delaySeconds), ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return false;
            }

            try
            {
                await PollAsync(ct).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[update] poll iteration failed: {ex.GetType().Name}: {ex.Message}");
            }

            delaySeconds = Math.Min(delaySeconds * 2, 120);
        }
        return !ct.IsCancellationRequested;
    }

    /// <summary>
    /// Derives the correct UpdateChannel from the running build's version when
    /// the version has changed since the last run. A prerelease build sets "beta";
    /// a stable build sets "production". No-ops when the version is unchanged,
    /// preserving a manual channel choice across restarts of the same build.
    /// </summary>
    private void SyncChannelToVersion()
    {
        var s = _store.Load();
        var (channel, changed) = ResolveChannelForBuild(
            s.Update.LastRunVersion,
            BuildInfo.Version,
            s.Update.UpdateChannel);

        if (!changed)
        {
            return;
        }

        _store.Update(settings =>
        {
            settings.Update.UpdateChannel = channel;
            settings.Update.LastRunVersion = BuildInfo.Version;
        });
    }

    /// <summary>
    /// Whether a detected newer release should be surfaced to the dashboard.
    /// Asset selection already filters by OS, so any resolved manifest carries
    /// a download link installable on this platform; the offer itself is not
    /// gated by whether the service can apply it (see <see cref="ShouldAutoStage"/>).
    /// </summary>
    internal static bool CanOfferUpdate(bool isNewer) => isNewer;

    /// <summary>
    /// Whether a background auto-stage (download+verify, mode "download" or
    /// "always") should start. An install that cannot apply updates (see
    /// <see cref="CanApplyUpdates"/>) never background-downloads; it only
    /// offers the manual DownloadUrl.
    /// </summary>
    internal static bool ShouldAutoStage(bool offerUpdate, string mode, bool alreadyStaged, bool canApply) =>
        offerUpdate && (mode is "download" or "always") && !alreadyStaged && canApply;

    /// <summary>Whether this install hands updates to an installer itself (Windows
    /// Inno Setup, Linux install.sh, macOS bundle swap) rather than linking the download.</summary>
    private static bool CanApplyUpdates => OperatingSystem.IsWindows() || UnixUpdateApplier.CanAutoInstall;

    /// <summary>
    /// Pure channel-derivation logic: given the persisted last-run version, the
    /// current build version, and the current channel, returns the channel that
    /// should be active and whether the settings need to be written.
    /// </summary>
    internal static (string channel, bool changed) ResolveChannelForBuild(
        string lastRunVersion,
        string buildVersion,
        string currentChannel)
    {
        if (string.Equals(lastRunVersion, buildVersion, StringComparison.Ordinal))
        {
            return (currentChannel, false);
        }

        var channel = VersionCompare.IsPrerelease(buildVersion) ? "beta" : "production";
        return (channel, true);
    }

    /// <summary>
    /// Applies or diagnoses a staged install marker on startup.
    /// Success (version advanced): sets JustUpdatedTo and deletes the marker. Dashboard reopen
    /// is handled by the helper reading the flag file written before the installer was launched.
    /// Pending + always mode: re-verifies and launches the staged installer, then stops the service.
    /// Pending + other modes: no-op (notify/download wait for an explicit user trigger).
    /// Attempted but version did not advance: boot-loop guard; clears marker and sets failed state.
    /// </summary>
    private void ApplyPendingOnStartup(CancellationToken ct)
    {
        var s = _store.Load();

        // Lock the staging dir before trusting anything in it: a non-admin can
        // otherwise pre-create it under user-writable %ProgramData% and plant a
        // marker/installer the SYSTEM apply path would run.
        try { UpdateDownloader.EnsureSecureStagingDir(); } catch { }

        var marker = StagedInstallMarkerStore.Read();
        if (marker is null)
        {
            return;
        }

        // Current version is at or beyond the marker's target: install succeeded.
        if (!VersionCompare.IsNewer(marker.Version, BuildInfo.Version))
        {
            // Ticks written before the string so any reader that observes the
            // non-empty string sees the already-committed ticks value.
            Volatile.Write(ref _justUpdatedToSetAtTicks, DateTime.UtcNow.Ticks);
            Volatile.Write(ref _justUpdatedTo, BuildInfo.Version);
            StagedInstallMarkerStore.Delete();
            UnixUpdateApplier.DeleteExtractedPayload();
            return;
        }

        // Marker names a version newer than what is running.
        if (marker.State == StagedInstallMarkerStore.StatePending)
        {
            if (s.Update.UpdateMode == "always")
            {
                // Downloaded and verified but not yet launched. Apply it now.
                ApplyPendingInstall(marker, s, ct);
            }
            // notify/download: staged installer waits for POST /update/start.
            return;
        }

        // State is "attempted" (or any unrecognized value): the installer was
        // launched but the version did not advance. Boot-loop guard.
        Console.Error.WriteLine($"[update] staged install of {marker.Version} did not advance version; treating as failed");
        StagedInstallMarkerStore.Delete();

        _status = new UpdateStatusResponse
        {
            CurrentVersion = BuildInfo.Version,
            LatestVersion = marker.Version,
            UpdateAvailable = true,
            Channel = string.IsNullOrEmpty(s.Update.UpdateChannel) ? "production" : s.Update.UpdateChannel,
            UpdateMode = s.Update.UpdateMode,
            ReleaseNotes = "",
            LastCheckedUnix = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            LastCheckError = $"Install of {marker.Version} did not complete.",
            State = "failed",
            UpdateReady = false,
            CanAutoInstall = CanApplyUpdates,
        };
    }

    private void ApplyPendingInstall(StagedInstallMarker marker, NexusSettings s, CancellationToken ct)
    {
        if (!CanApplyUpdates)
        {
            Console.Error.WriteLine($"[update] pending install of {marker.Version} skipped: this install cannot apply updates");
            StagedInstallMarkerStore.Delete();
            return;
        }

        try
        {
            var channel = string.IsNullOrEmpty(s.Update.UpdateChannel) ? "production" : s.Update.UpdateChannel;
            Console.Error.WriteLine($"[update] applying pending install of {marker.Version} on startup");

            if (!File.Exists(marker.InstallerPath))
            {
                Console.Error.WriteLine($"[update] pending installer not found at {marker.InstallerPath}; skipping auto-apply");
                StagedInstallMarkerStore.Delete();
                return;
            }

            // The marker.Sha256 was sourced from SHA256SUMS (required for the auto-stage
            // path); re-verify before launching to detect staged-file tampering.
            UpdateIntegrity.VerifyAsync(marker.InstallerPath, marker.Sha256, ct).GetAwaiter().GetResult();

            var reopenDashboard = marker.ReopenDashboard || s.Update.UpdateMode == "always";

            // Flip to "attempted" before launching so a crash here is not retried.
            StagedInstallMarkerStore.Write(new StagedInstallMarker
            {
                Version = marker.Version,
                InstallerPath = marker.InstallerPath,
                Sha256 = marker.Sha256,
                State = StagedInstallMarkerStore.StateAttempted,
                ReopenDashboard = reopenDashboard,
            });

#if WINDOWS
            if (reopenDashboard)
            {
                DashboardReopenFlag.Write();
            }
#endif

            _status = new UpdateStatusResponse
            {
                CurrentVersion = BuildInfo.Version,
                LatestVersion = marker.Version,
                UpdateAvailable = true,
                Channel = channel,
                UpdateMode = s.Update.UpdateMode,
                ReleaseNotes = "",
                LastCheckedUnix = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                LastCheckError = "",
                State = "installing",
                UpdateReady = false,
                CanAutoInstall = true,
            };

#if WINDOWS
            var launched = UpdateInstaller.LaunchViaSchtasks(marker.InstallerPath, marker.Version);
            if (!launched)
            {
                throw new InvalidOperationException("schtasks /Run did not succeed.");
            }
#else
            UnixUpdateApplier.Apply(marker.InstallerPath, marker.Version);
#endif

            // Don't self-stop: the installer's `net stop` owns the stop. If the
            // installer can't proceed, the service keeps running the old version
            // instead of being left dead.
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[update] startup apply failed: {ex.GetType().Name}: {ex.Message}");
            StagedInstallMarkerStore.Delete();

            var channel = string.IsNullOrEmpty(s.Update.UpdateChannel) ? "production" : s.Update.UpdateChannel;
            _status = new UpdateStatusResponse
            {
                CurrentVersion = BuildInfo.Version,
                LatestVersion = marker.Version,
                UpdateAvailable = true,
                Channel = channel,
                UpdateMode = s.Update.UpdateMode,
                ReleaseNotes = "",
                LastCheckedUnix = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                LastCheckError = $"Auto-apply failed: {ex.GetType().Name}: {ex.Message}",
                State = "failed",
                UpdateReady = false,
                CanAutoInstall = true,
            };
        }
    }

    private static async Task<bool> WaitAsync(PeriodicTimer timer, CancellationToken ct)
    {
        try
        {
            return await timer.WaitForNextTickAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return false;
        }
    }

    /// <summary>
    /// Triggers an immediate check (called by POST /update/check). Always returns
    /// a status snapshot; network errors are surfaced in lastCheckError.
    /// </summary>
    public async Task<UpdateStatusResponse> CheckNowAsync(CancellationToken ct)
    {
        // POST /update/check reaches this without passing ExecuteAsync.
        if (!Common.ClientCredential.IsOfficial)
        {
            return _status;
        }

        await PollAsync(ct).ConfigureAwait(false);
        return _status;
    }

    /// <summary>
    /// Starts a download-verify-install sequence. Returns false with a reason
    /// string when blocked (mid-flash, already installing, not newer, no hash).
    ///
    /// When a staged installer is already ready, skips the download and goes
    /// straight to re-verify + launch.
    /// </summary>
    public (bool started, string reason) StartUpdate(string? requiredVersion, bool reopenAfter = false)
    {
        if (!Common.ClientCredential.IsOfficial)
        {
            return (false, "Updates are managed outside this build.");
        }

        if (_flasher.IsFlashing)
        {
            return (false, "A firmware update is in progress.");
        }

        if (_latestManifest is null)
        {
            return (false, "No update manifest available. Run a check first.");
        }

        if (!string.IsNullOrEmpty(requiredVersion) &&
            !string.Equals(requiredVersion, _latestManifest.Version, StringComparison.OrdinalIgnoreCase))
        {
            return (false, $"Version mismatch: manifest is {_latestManifest.Version}, requested {requiredVersion}.");
        }

        if (!VersionCompare.IsNewer(_latestManifest.Version, BuildInfo.Version))
        {
            return (false, $"{_latestManifest.Version} is not newer than the running {BuildInfo.Version}.");
        }

        if (string.IsNullOrEmpty(_latestManifest.Sha256))
        {
            return (false, "Cannot install: no SHA-256 hash available for the update.");
        }

        if (Interlocked.CompareExchange(ref _installing, 1, 0) != 0)
        {
            // A background auto-stage holds the lock and nothing is staged yet:
            // adopt it so this trigger launches once staging finishes, rather
            // than rejecting the user mid-download. A click in the sub-ms window
            // where staging is flipping _updateReady true is acked but not
            // launched; status reverts to "ready" and the user re-triggers.
            if (!_updateReady && _progress.Active)
            {
                _launchAfterStageReopen = reopenAfter;
                _launchAfterStage = true;
                return (true, "");
            }
            return (false, "An install is already in progress.");
        }

        var cts = new CancellationTokenSource();
        _installCts = cts;

        // If we already have a staged installer, use the fast path.
        if (_updateReady && _stagedInstallerPath is not null)
        {
            _ = Task.Run(() => RunLaunchStagedAsync(_latestManifest, _stagedInstallerPath, reopenAfter, cts.Token));
        }
        else
        {
            _ = Task.Run(() => RunInstallAsync(_latestManifest, launchAfterVerify: true, reopenAfter, cts.Token));
        }

        return (true, "");
    }

    // --- Internal ---

    private async Task PollAsync(CancellationToken ct)
    {
        var s = _store.Load();
        var channel = string.IsNullOrEmpty(s.Update.UpdateChannel)
            ? "production"
            : s.Update.UpdateChannel;

        SetStatusChecking(channel, s);

        try
        {
            var manifest = await _source.GetLatestAsync(channel, ct).ConfigureAwait(false);
            _hadSuccessfulCheck = true;

            _latestManifest = manifest;

            var snapshot = _store.Load();
            var isNewer = manifest is not null && VersionCompare.IsNewer(manifest.Version, BuildInfo.Version);
            var offerUpdate = CanOfferUpdate(isNewer);

            var mode = snapshot.Update.UpdateMode;
            _status = new UpdateStatusResponse
            {
                CurrentVersion = BuildInfo.Version,
                LatestVersion = manifest?.Version ?? BuildInfo.Version,
                UpdateAvailable = offerUpdate,
                Channel = channel,
                UpdateMode = mode,
                ReleaseNotes = manifest?.Notes ?? "",
                LastCheckedUnix = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                LastCheckError = "",
                State = _installing == 1 ? GetInstallStateString() : (_updateReady ? "ready" : "idle"),
                UpdateReady = _updateReady,
                PublishedAtUnix = manifest?.PublishedAt?.ToUnixTimeSeconds() ?? 0,
                DownloadUrl = manifest?.AssetUrl ?? "",
                CanAutoInstall = CanApplyUpdates,
            };

            // "download" stages a download+verify but does not install.
            // "always" downloads, verifies, and installs automatically.
            // "notify" only detects; no background download.
            // Both "download" and "always" auto-stage (download+verify) but never
            // launch mid-session. "always" applies on the next restart via
            // ApplyPendingOnStartup; "download" waits for POST /update/start.
            // Re-stage when the latest version differs from what's already
            // staged: a release published while an update was queued supersedes
            // it, so the staged installer + marker must follow the new version.
            var alreadyStaged = _updateReady
                && string.Equals(_stagedVersion, manifest?.Version, StringComparison.OrdinalIgnoreCase);
            if (ShouldAutoStage(offerUpdate, mode, alreadyStaged, CanApplyUpdates) && manifest is not null)
            {
                if (Interlocked.CompareExchange(ref _installing, 1, 0) == 0)
                {
                    if (_updateReady)
                    {
                        // A previous version is staged but superseded; the download
                        // below prunes its installer, so drop the ready state + its
                        // marker now. Status reverts to "available" until the new
                        // version finishes staging - never "ready" with no file.
                        _updateReady = false;
                        _stagedInstallerPath = null;
                        _stagedVersion = null;
                        StagedInstallMarkerStore.Delete();
                        UpdateStatusUpdateReady(false);
                    }
                    var cts = new CancellationTokenSource();
                    _installCts = cts;
                    _ = Task.Run(() => RunInstallAsync(manifest, launchAfterVerify: false, reopenAfter: false, cts.Token));
                    Console.Error.WriteLine($"[update] auto-staging download for {manifest.Version} (mode={mode})");
                }
            }

            // Push on the availability rising edge so the sidebar banner appears
            // immediately in notify mode; download/always also fire here, then
            // again from RunInstallAsync once the staged installer is ready.
            NotifyStatusChanged();
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            var snapshot = _store.Load();
            _status = new UpdateStatusResponse
            {
                CurrentVersion = BuildInfo.Version,
                LatestVersion = _latestManifest?.Version ?? BuildInfo.Version,
                UpdateAvailable = CanOfferUpdate(
                    _latestManifest is not null && VersionCompare.IsNewer(_latestManifest.Version, BuildInfo.Version)),
                Channel = channel,
                UpdateMode = snapshot.Update.UpdateMode,
                ReleaseNotes = _latestManifest?.Notes ?? "",
                LastCheckedUnix = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                LastCheckError = $"{ex.GetType().Name}: {ex.Message}",
                State = _installing == 1 ? GetInstallStateString() : (_updateReady ? "ready" : "idle"),
                UpdateReady = _updateReady,
                PublishedAtUnix = _latestManifest?.PublishedAt?.ToUnixTimeSeconds() ?? 0,
                DownloadUrl = _latestManifest?.AssetUrl ?? "",
                CanAutoInstall = CanApplyUpdates,
            };
            Console.Error.WriteLine($"[update] check failed: {ex.GetType().Name}: {ex.Message}");
        }
    }

    private void SetStatusChecking(string channel, NexusSettings s)
    {
        _status = new UpdateStatusResponse
        {
            CurrentVersion = BuildInfo.Version,
            LatestVersion = _latestManifest?.Version ?? BuildInfo.Version,
            UpdateAvailable = CanOfferUpdate(
                _latestManifest is not null && VersionCompare.IsNewer(_latestManifest.Version, BuildInfo.Version)),
            Channel = channel,
            UpdateMode = s.Update.UpdateMode,
            ReleaseNotes = _latestManifest?.Notes ?? "",
            LastCheckedUnix = _status.LastCheckedUnix,
            LastCheckError = "",
            State = "checking",
            UpdateReady = _updateReady,
            PublishedAtUnix = _latestManifest?.PublishedAt?.ToUnixTimeSeconds() ?? 0,
            DownloadUrl = _latestManifest?.AssetUrl ?? "",
            CanAutoInstall = CanApplyUpdates,
        };
    }

    private string GetInstallStateString()
    {
        return _progress.Phase switch
        {
            "downloading" => "downloading",
            "verifying"   => "verifying",
            "launching"   => "installing",
            "installing"  => "installing",
            "failed"      => "failed",
            _             => "idle",
        };
    }

    /// <summary>
    /// Re-verify the staged installer and launch it. Called when the user
    /// triggers POST /update/start and a staged installer is already ready.
    /// </summary>
    private async Task RunLaunchStagedAsync(UpdateManifest manifest, string installerPath, bool reopenAfter, CancellationToken ct)
    {
        try
        {
            SetProgress("verifying", 99, "Verifying staged installer...", manifest.Version);

            if (!File.Exists(installerPath))
            {
                // Staged file is gone; fall through to a full re-download.
                _updateReady = false;
                _stagedInstallerPath = null;
                _stagedVersion = null;
                await RunInstallAsync(manifest, launchAfterVerify: true, reopenAfter, ct).ConfigureAwait(false);
                return;
            }

            await UpdateIntegrity.VerifyAsync(installerPath, manifest.Sha256!, ct).ConfigureAwait(false);

#if WINDOWS
            StagedInstallMarkerStore.Write(new StagedInstallMarker
            {
                Version = manifest.Version,
                InstallerPath = installerPath,
                Sha256 = manifest.Sha256 ?? "",
                State = StagedInstallMarkerStore.StateAttempted,
                ReopenDashboard = reopenAfter,
            });

            if (reopenAfter)
            {
                DashboardReopenFlag.Write();
            }

            try
            {
                await TrayCommands.ShowUpdaterWindowAsync(
                    _helperRegistry,
                    fromVersion: BuildInfo.Version,
                    toVersion: manifest.Version).ConfigureAwait(false);
            }
            catch { /* non-critical; installer proceeds regardless */ }

            var launched = UpdateInstaller.LaunchViaSchtasks(installerPath, manifest.Version);
            if (!launched)
            {
                throw new InvalidOperationException("schtasks /Run did not succeed.");
            }

            // Installer is running detached now; only here signal the UI to expect
            // a restart. A failed launch above never reaches "launching", so the UI
            // shows "failed" rather than waiting to reconnect to a service that
            // never stopped. The installer's `net stop` does the actual stop.
            SetProgress("launching", 100, "Launching installer...", manifest.Version);
            UpdateStatusState("installing");
            SetProgress("installing", 100, "Installing...", manifest.Version);
#else
            LaunchUnixInstall(manifest, installerPath);
#endif
        }
        catch (OperationCanceledException)
        {
            SetProgressFailed("Install cancelled.", manifest.Version);
#if WINDOWS
            try { _ = TrayCommands.CloseUpdaterWindowAsync(_helperRegistry); } catch { }
#endif
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[update] staged launch failed: {ex.GetType().Name}: {ex.Message}");
            SetProgressFailed($"{ex.GetType().Name}: {ex.Message}", manifest.Version);
            UpdateStatusState("failed");
#if WINDOWS
            try { _ = TrayCommands.CloseUpdaterWindowAsync(_helperRegistry); } catch { }
#endif
        }
        finally
        {
            Interlocked.Exchange(ref _installing, 0);
            _launchAfterStage = false;
            _launchAfterStageReopen = false;
            _installCts?.Dispose();
            _installCts = null;
        }
    }

    /// <summary>
    /// Download and verify the installer. When <paramref name="launchAfterVerify"/>
    /// is true, also write the marker and launch via schtasks (the installer's
    /// net stop stops the service).
    /// When false, set <c>_updateReady</c> and send the tray notification.
    /// </summary>
    private async Task RunInstallAsync(UpdateManifest manifest, bool launchAfterVerify, bool reopenAfter, CancellationToken ct)
    {
        try
        {
            // Phase: downloading
            SetProgress("downloading", 0, $"Downloading {manifest.Version}...", manifest.Version);

            string? installerPath = null;
            var totalSize = manifest.AssetSize > 0 ? manifest.AssetSize : 1;
            var progress = new Progress<long>(bytesReceived =>
            {
                var pct = Math.Min(99, (double)bytesReceived / totalSize * 100.0);
                SetProgress("downloading", pct, $"Downloading {manifest.Version}...", manifest.Version);
            });

            installerPath = await _downloader.DownloadAsync(manifest, progress, ct).ConfigureAwait(false);

            // Phase: verifying
            SetProgress("verifying", 99, "Verifying installer...", manifest.Version);
            await UpdateIntegrity.VerifyAsync(installerPath, manifest.Sha256!, ct).ConfigureAwait(false);

            var adopted = _launchAfterStage;
            if (!launchAfterVerify && !adopted)
            {
                if (!manifest.Sha256IsFromSumsFile)
                {
                    // SHA256SUMS not published for this release. Auto-stage requires the
                    // author-published hash; the asset digest is corruption-only, not authenticity.
                    Console.Error.WriteLine($"[update] skipping auto-stage of {manifest.Version}: SHA256SUMS not available");
                    TryDeleteStagedFile(installerPath);
                    return;
                }

                // Stage only: signal ready, write pending marker, send tray notification.
                _stagedInstallerPath = installerPath;
                _stagedVersion = manifest.Version;
                _updateReady = true;
                StagedInstallMarkerStore.Write(new StagedInstallMarker
                {
                    Version = manifest.Version,
                    InstallerPath = installerPath,
                    Sha256 = manifest.Sha256 ?? "",
                    State = StagedInstallMarkerStore.StatePending,
                });
                UpdateStatusState("ready");
                UpdateStatusUpdateReady(true);
                // Auto-stage left _progress at an active "verifying"; clear it so
                // a modal opened before a user-triggered install shows the notes
                // view rather than a stale, stuck progress bar.
                _progress = new UpdateProgressResponse { Version = manifest.Version };

#if WINDOWS
                try
                {
                    await TrayCommands.UpdateReadyAsync(_helperRegistry, manifest.Version).ConfigureAwait(false);
                }
                catch { /* notification is non-critical */ }
#endif
                // Staging finished: push so a download/always dashboard surfaces
                // the banner now, alongside the tray "update ready" notification.
                NotifyStatusChanged();
                Console.Error.WriteLine($"[update] download staged for {manifest.Version}; awaiting user trigger");
                return;
            }

            // A user trigger adopted this background stage: honor its reopen intent.
            if (adopted)
            {
                reopenAfter = reopenAfter || _launchAfterStageReopen;
            }

#if WINDOWS
            StagedInstallMarkerStore.Write(new StagedInstallMarker
            {
                Version = manifest.Version,
                InstallerPath = installerPath,
                Sha256 = manifest.Sha256 ?? "",
                State = StagedInstallMarkerStore.StateAttempted,
                ReopenDashboard = reopenAfter,
            });

            if (reopenAfter)
            {
                DashboardReopenFlag.Write();
            }

            try
            {
                await TrayCommands.ShowUpdaterWindowAsync(
                    _helperRegistry,
                    fromVersion: BuildInfo.Version,
                    toVersion: manifest.Version).ConfigureAwait(false);
            }
            catch { /* non-critical; installer proceeds regardless */ }

            var launched = UpdateInstaller.LaunchViaSchtasks(installerPath, manifest.Version);
            if (!launched)
            {
                throw new InvalidOperationException("schtasks /Run did not succeed.");
            }

            // Installer running detached. Only now signal the UI to expect a
            // restart; a failed launch above goes to "failed" instead. The
            // installer's `net stop` does the actual stop - we don't self-stop, so
            // an installer that can't proceed leaves the old version running.
            SetProgress("launching", 100, "Launching installer...", manifest.Version);
            UpdateStatusState("installing");
            SetProgress("installing", 100, "Installing...", manifest.Version);
#else
            LaunchUnixInstall(manifest, installerPath);
#endif
        }
        catch (OperationCanceledException)
        {
            SetProgressFailed("Install cancelled.", manifest.Version);
#if WINDOWS
            try { _ = TrayCommands.CloseUpdaterWindowAsync(_helperRegistry); } catch { }
#endif
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[update] install failed: {ex.GetType().Name}: {ex.Message}");
            SetProgressFailed($"{ex.GetType().Name}: {ex.Message}", manifest.Version);
            UpdateStatusState("failed");
#if WINDOWS
            try { _ = TrayCommands.CloseUpdaterWindowAsync(_helperRegistry); } catch { }
#endif
        }
        finally
        {
            Interlocked.Exchange(ref _installing, 0);
            _launchAfterStage = false;
            _launchAfterStageReopen = false;
            _installCts?.Dispose();
            _installCts = null;
        }
    }

#if !WINDOWS
    private void LaunchUnixInstall(UpdateManifest manifest, string installerPath)
    {
        StagedInstallMarkerStore.Write(new StagedInstallMarker
        {
            Version = manifest.Version,
            InstallerPath = installerPath,
            Sha256 = manifest.Sha256 ?? "",
            State = StagedInstallMarkerStore.StateAttempted,
        });
        try
        {
            UnixUpdateApplier.Apply(installerPath, manifest.Version);
        }
        catch
        {
            // Nothing was launched, so the next boot has no install to judge.
            StagedInstallMarkerStore.Delete();
            throw;
        }
        SetProgress("launching", 100, "Launching installer...", manifest.Version);
        UpdateStatusState("installing");
        SetProgress("installing", 100, "Installing...", manifest.Version);
    }
#endif

    private static void TryDeleteStagedFile(string path)
    {
        try { File.Delete(path); } catch { }
    }

    private void SetProgress(string phase, double percent, string message, string version)
    {
        _progress = new UpdateProgressResponse
        {
            Active = true,
            Phase = phase,
            Percent = percent,
            Message = message,
            Version = version,
            Success = false,
            Error = "",
        };
    }

    private void SetProgressFailed(string error, string version)
    {
        _progress = new UpdateProgressResponse
        {
            Active = false,
            Phase = "failed",
            Percent = 0,
            Message = error,
            Version = version,
            Success = false,
            Error = error,
        };
    }

    private void UpdateStatusState(string state)
    {
        _status = new UpdateStatusResponse
        {
            CurrentVersion = _status.CurrentVersion,
            LatestVersion = _status.LatestVersion,
            UpdateAvailable = _status.UpdateAvailable,
            Channel = _status.Channel,
            UpdateMode = _status.UpdateMode,
            ReleaseNotes = _status.ReleaseNotes,
            LastCheckedUnix = _status.LastCheckedUnix,
            LastCheckError = _status.LastCheckError,
            State = state,
            UpdateReady = _updateReady,
            PublishedAtUnix = _status.PublishedAtUnix,
            DownloadUrl = _status.DownloadUrl,
            CanAutoInstall = _status.CanAutoInstall,
        };
    }

    /// <summary>
    /// Push an update-status-changed frame on the rising edge of updateAvailable
    /// or updateReady so subscribed dashboards refetch GET /update/status and
    /// surface the banner on detection. Both signals falling re-arms the rise.
    /// </summary>
    private void NotifyStatusChanged()
    {
        var snap = _status;
        bool fire;
        lock (_broadcastLock)
        {
            fire = (snap.UpdateAvailable && !_broadcastUpdateAvailable)
                || (snap.UpdateReady && !_broadcastUpdateReady);
            _broadcastUpdateAvailable = snap.UpdateAvailable;
            _broadcastUpdateReady = snap.UpdateReady;
        }
        if (fire)
        {
            PanelTopics.BroadcastUpdate(_hub);
        }
    }

    private void UpdateStatusUpdateReady(bool ready)
    {
        _status = new UpdateStatusResponse
        {
            CurrentVersion = _status.CurrentVersion,
            LatestVersion = _status.LatestVersion,
            UpdateAvailable = _status.UpdateAvailable,
            Channel = _status.Channel,
            UpdateMode = _status.UpdateMode,
            ReleaseNotes = _status.ReleaseNotes,
            LastCheckedUnix = _status.LastCheckedUnix,
            LastCheckError = _status.LastCheckError,
            State = _status.State,
            UpdateReady = ready,
            PublishedAtUnix = _status.PublishedAtUnix,
            DownloadUrl = _status.DownloadUrl,
            CanAutoInstall = _status.CanAutoInstall,
        };
    }
}
