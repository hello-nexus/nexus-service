using System;
using System.Collections.Concurrent;
using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Nexus.Service.Common.ExternalTools;
using Nexus.Service.Models.Widgets;
using Nexus.Service.Widgets;

namespace Nexus.Service.Store;

/// <summary>
/// Installs a versioned <c>.nexus-app</c> from the asset CDN into the user apps
/// root. The artifact is a zip whose root is the app directory, so it extracts
/// straight into <c>apps/&lt;id&gt;</c>.
/// </summary>
/// <remarks>
/// The caller supplies an id, a version and the expected hash from the store
/// catalog; the URL is composed here, so a page can never aim the download at
/// another host. The hash is the trust pin - an artifact that does not match is
/// deleted, never installed - and the extracted manifest has to agree with what
/// was asked for, or a mislabeled artifact would install under the wrong id.
/// </remarks>
public sealed class StoreInstaller
{
    private readonly HttpClient _http;
    private readonly AppRegistry _registry;
    private readonly Func<string?> _userRoot;

    // One install per app at a time: two installs of one app share its staging paths and swap.
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _gates = new(StringComparer.Ordinal);

    /// <summary>Default asset host. NEXUS_STORE_ASSETS points it elsewhere for local testing.</summary>
    public const string DefaultAssetsBase = "https://assets.hellonexus.com";

    public StoreInstaller(HttpClient http, AppRegistry registry)
        : this(http, registry, DefaultUserRoot)
    {
    }

    /// <summary>Test seam: supplies the user apps root.</summary>
    public StoreInstaller(HttpClient http, AppRegistry registry, Func<string?> userRoot)
    {
        _http = http;
        _registry = registry;
        _userRoot = userRoot;
    }

    public static string AssetsBase()
    {
        var configured = Environment.GetEnvironmentVariable("NEXUS_STORE_ASSETS");
        return string.IsNullOrWhiteSpace(configured) ? DefaultAssetsBase : configured.TrimEnd('/');
    }

    public static string ArtifactUrl(string appId, string version) =>
        $"{AssetsBase()}/apps/{appId}/{version}.nexus-app";

    /// <summary>
    /// Semver, tight enough to sit in a URL path segment: digits, dots, and an
    /// optional prerelease of ASCII word characters.
    /// </summary>
    public static bool IsValidVersion(string? version)
    {
        if (string.IsNullOrEmpty(version) || version.Length > 64) return false;
        var dashSeen = false;
        var digits = 0;
        var dots = 0;
        foreach (var c in version)
        {
            if (c >= '0' && c <= '9') { digits++; continue; }
            if (c == '.') { if (!dashSeen) dots++; continue; }
            if (c == '-') { dashSeen = true; continue; }
            if (dashSeen && (char.IsAsciiLetterOrDigit(c) || c == '.')) continue;
            return false;
        }
        return digits > 0 && dots == 2 && version[0] != '.' && version[^1] != '.';
    }

    public Task<StoreInstallResponse> InstallAsync(StoreInstallRequest req, CancellationToken ct) =>
        GatedAsync(req, replaceOnly: false, ct);

    /// <summary>Installs over an app already on disk, and refuses (not_installed) once it is gone, so an uninstall that lands mid-download stays uninstalled.</summary>
    public Task<StoreInstallResponse> UpdateAsync(StoreInstallRequest req, CancellationToken ct) =>
        GatedAsync(req, replaceOnly: true, ct);

    private async Task<StoreInstallResponse> GatedAsync(StoreInstallRequest req, bool replaceOnly, CancellationToken ct)
    {
        var gate = _gates.GetOrAdd(req.AppId ?? "", _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct).ConfigureAwait(false);
        try
        { return await InstallCoreAsync(req, replaceOnly, ct).ConfigureAwait(false); }
        finally
        { gate.Release(); }
    }

    private async Task<StoreInstallResponse> InstallCoreAsync(StoreInstallRequest req, bool replaceOnly, CancellationToken ct)
    {
        var appId = req.AppId ?? "";
        var version = req.Version ?? "";
        var fail = (string reason) => new StoreInstallResponse { AppId = appId, Version = version, Ok = false, Reason = reason };

        if (!AppIds.IsValid(appId)) return fail("invalid_app_id");
        if (!IsValidVersion(version)) return fail("invalid_version");
        if (string.IsNullOrWhiteSpace(req.Sha256)) return fail("missing_hash");

        var userRoot = _userRoot();
        if (string.IsNullOrEmpty(userRoot)) return fail("no_user_apps_dir");

        // Stage beside the destination so the final swap is a rename on the same
        // volume, and a failed install never leaves a half-written app behind.
        var staging = Path.Combine(userRoot, $".{appId}.{version}.staging");
        var archive = staging + ".nexus-app";
        try
        {
            AppInstallPaths.SecureUserRoots();
            Directory.CreateDirectory(userRoot);
            CleanUp(staging, archive);

            try
            {
                await VerifiedDownload.DownloadAsync(_http, ArtifactUrl(appId, version), archive, req.Sha256!, req.Size, ct)
                    .ConfigureAwait(false);
            }
            catch (InvalidDataException)
            {
                return fail("hash_mismatch");
            }
            catch (HttpRequestException)
            {
                return fail("artifact_unavailable");
            }

            Directory.CreateDirectory(staging);
            try
            {
                // Refuses entries that escape the destination, so a hostile
                // archive cannot write outside the staging dir.
                ZipFile.ExtractToDirectory(archive, staging, overwriteFiles: true);
            }
            catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException)
            {
                return fail("bad_archive");
            }

            if (!ManifestAgrees(staging, appId, version)) return fail("manifest_mismatch");

            var dest = Path.Combine(userRoot, appId);
            if (replaceOnly && !Directory.Exists(dest)) return fail("not_installed");
            // Moved aside, not deleted: a file the service is streaming from the app
            // (Windows) fails the rename and leaves the installed copy whole, where a
            // recursive delete would stop halfway.
            var aside = Path.Combine(userRoot, $".{appId}.replaced");
            DeleteDir(aside);
            if (Directory.Exists(dest)) Directory.Move(dest, aside);
            try
            { Directory.Move(staging, dest); }
            catch
            {
                try
                { if (Directory.Exists(aside) && !Directory.Exists(dest)) Directory.Move(aside, dest); }
                catch (Exception rollback)
                { Console.Error.WriteLine($"[store] {appId} left in {aside}: {rollback.Message}"); }
                throw;
            }
            DeleteDir(aside);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[store] install {appId}@{version} failed: {ex.GetType().Name}: {ex.Message}");
            return fail("install_failed");
        }
        finally
        {
            CleanUp(staging, archive);
        }

        _registry.Refresh();
        return new StoreInstallResponse { AppId = appId, Version = version, Ok = true };
    }

    /// <summary>The artifact has to describe the app that was asked for.</summary>
    private static bool ManifestAgrees(string dir, string appId, string version)
    {
        var path = Path.Combine(dir, "manifest.json");
        if (!File.Exists(path)) return false;
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            var root = doc.RootElement;
            return root.TryGetProperty("id", out var id)
                && root.TryGetProperty("version", out var ver)
                && id.ValueKind == JsonValueKind.String
                && ver.ValueKind == JsonValueKind.String
                && string.Equals(id.GetString(), appId, StringComparison.Ordinal)
                && string.Equals(ver.GetString(), version, StringComparison.Ordinal);
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static void CleanUp(string staging, string archive)
    {
        DeleteDir(staging);
        try { if (File.Exists(archive)) File.Delete(archive); } catch { /* best effort */ }
    }

    private static void DeleteDir(string dir)
    {
        try { if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true); } catch { /* best effort */ }
    }

    private static string? DefaultUserRoot()
    {
        foreach (var root in AppInstallPaths.Enumerate())
        {
            if (root.Source == AppInstallPaths.Source.User) return root.Path;
        }
        return null;
    }
}
