using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Nexus.Service.Games;
using Nexus.Service.Models.Lighting;
using Nexus.Service.Persistence;
using Nexus.Service.Serialization;

namespace Nexus.Service.Lighting;

public sealed class GameSyncGameScanner
{
    // Per-file caps for the byte scan. The read is streamed (StreamReadBufferSize),
    // so these bound IO time, not memory. Executables and DLLs get the larger cap
    // because a game may reference the SDK from nowhere else: Battlefield 6
    // bundles no Chroma DLL and its bf6.exe is 180 MB. Archives keep the smaller
    // one so a few root-level .pak/.dat files cannot eat MaxScanBytesPerGame
    // before the walk reaches the executable.
    private const long MaxScanBinarySizeBytes = 512L * 1024 * 1024;
    private const long MaxScanArchiveSizeBytes = 150L * 1024 * 1024;

    // Directory depth for the file-name pass. Unreal games keep the Razer plugin
    // DLL at <Project>/Plugins/ChromaSDKPlugin/Binaries/Win64/, depth 5 (Hogwarts
    // Legacy); 6 leaves one level of headroom.
    private const int NameScanDepth = 6;

    // The byte-scan pass stays shallower: it is budgeted (MaxScanFilesPerGame),
    // and a deeper walk would spend that budget on engine ThirdParty DLLs before
    // reaching the game's own binaries.
    private const int ByteScanDepth = 4;

    // Per-game scan budget. A Steam library with many large games can easily reach
    // tens of GB if every .pak is read; cap both axes so a single scan stays bounded.
    private const int MaxScanFilesPerGame = 400;
    private const long MaxScanBytesPerGame = 2L * 1024 * 1024 * 1024; // 2 GB

    // Streamed read buffer: avoids allocating the full file in the LOH.
    private const int StreamReadBufferSize = 65536;

    private readonly ILogger<GameSyncGameScanner> _logger;
    private readonly object _lock = new();
    private volatile bool _scanning;
    private long _scannedAtEpoch;
    private IReadOnlyList<DetectedGame> _games = Array.Empty<DetectedGame>();
    private int _scanRunning;

    private readonly string? _cachePath;

    /// <param name="cachePath">Overrides the machine cache file; tests pass a temp path so a run never touches the real one.</param>
    public GameSyncGameScanner(ILogger<GameSyncGameScanner> logger, string? cachePath = null)
    {
        _logger = logger;
        _cachePath = cachePath;
        LoadCache();
    }

    // A scan lives only in memory, so every restart used to leave the Game Sync
    // tab empty for the length of a fresh one. The last result is mirrored here
    // instead: machine-local under the data root's cache/, never settings.json,
    // so it is not part of the profile that syncs to the cloud.
    private string CachePath()
        => _cachePath ?? Path.Combine(NexusDataPaths.NexusRoot(), "cache", "game-sync-games.json");

    private void LoadCache()
    {
        try
        {
            var path = CachePath();
            if (!File.Exists(path)) return;
            var cached = JsonSerializer.Deserialize(File.ReadAllText(path), AppJsonContext.Default.GameSyncScanCache);
            if (cached?.Games is null || cached.ScannedAt <= 0) return;
            lock (_lock)
            {
                _games = cached.Games.AsReadOnly();
                _scannedAtEpoch = cached.ScannedAt;
            }
            _logger.LogInformation("[game-sync-scanner] restored {GameCount} game(s) from the last scan", cached.Games.Count);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[game-sync-scanner] cannot read the cached scan");
        }
    }

    private void SaveCache(List<DetectedGame> games, long scannedAt)
    {
        try
        {
            var path = CachePath();
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var payload = new GameSyncScanCache { ScannedAt = scannedAt, Games = games };
            File.WriteAllText(path, JsonSerializer.Serialize(payload, AppJsonContext.Default.GameSyncScanCache));
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[game-sync-scanner] cannot write the cached scan");
        }
    }

    // Called on the scan thread after results are published. Used to trigger
    // side-effects (e.g. cfg ensure) without coupling the scanner to providers.
    public Action<IReadOnlyList<DetectedGame>>? OnScanComplete { get; set; }

    public bool Scanning => _scanning;
    public long? ScannedAt => _scannedAtEpoch == 0 ? null : _scannedAtEpoch;
    public IReadOnlyList<DetectedGame> Games => _games;

    public void RequestScan(CancellationToken ct = default)
    {
        if (Interlocked.CompareExchange(ref _scanRunning, 1, 0) != 0)
        {
            return;
        }

        Task.Run(() => RunScan(ct), ct);
    }

    // Re-scan freshness window. The /games route (this method's only caller) is
    // hit when the Game Sync surface loads, so this fires on view, not on a timer.
    private const long StaleScanSeconds = 600;

    // Scans only when none has run yet or the last result is stale. Deduped by
    // RequestScan's run guard, so concurrent polls collapse to one scan.
    public void RequestScanIfStale(CancellationToken ct = default)
    {
        var at = ScannedAt;
        if (at is null || DateTimeOffset.UtcNow.ToUnixTimeSeconds() - at.Value > StaleScanSeconds)
        {
            RequestScan(ct);
        }
    }

    private void RunScan(CancellationToken ct)
    {
        _scanning = true;
        try
        {
            var candidates = InstalledGameCollectors.CollectAll(_logger);

            var results = new List<DetectedGame>(candidates.Count);
            foreach (var (name, installDir, store, appId) in candidates)
            {
                if (ct.IsCancellationRequested)
                {
                    break;
                }

                var emits = EmitsChroma(installDir, _logger, out var scanned, out var skipped);
                results.Add(new DetectedGame
                {
                    Name = name,
                    Store = store,
                    InstallDir = installDir,
                    AppId = appId,
                    EmitsChroma = emits,
                    EmitsGsi = store == "steam" && appId == "730",
                    ScannedFiles = scanned,
                    SkippedFiles = skipped,
                });
            }

            var scannedAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            lock (_lock)
            {
                _games = results.AsReadOnly();
                _scannedAtEpoch = scannedAt;
            }
            SaveCache(results, scannedAt);

            var emitterCount = results.Count(g => g.EmitsChroma);
            _logger.LogInformation("[game-sync-scanner] scan complete: {GameCount} games, {EmitterCount} emitters",
                results.Count, emitterCount);

            try { OnScanComplete?.Invoke(_games); }
            catch (Exception ex) { _logger.LogWarning(ex, "[game-sync-scanner] OnScanComplete callback faulted"); }
        }
        finally
        {
            _scanning = false;
            Interlocked.Exchange(ref _scanRunning, 0);
        }
    }

    internal static bool EmitsChroma(string installDir, ILogger logger, out int scannedFiles, out int skippedFiles)
    {
        scannedFiles = 0;
        skippedFiles = 0;

        if (!Directory.Exists(installDir))
        {
            return false;
        }

        // Step 1: bundled DLL/file name check - no byte reading needed.
        foreach (var file in EnumerateFilesDepthCapped(installDir, "*", NameScanDepth, logger))
        {
            var fileName = Path.GetFileName(file);
            if (IsBundledChromaFile(fileName))
            {
                return true;
            }
        }

        // Step 2: byte-scan candidate files for Chroma SDK strings.
        // Stops once either per-game budget is hit (MaxScanFilesPerGame or MaxScanBytesPerGame).
        var extensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            ".exe", ".dll", ".hdll", ".hl", ".dat", ".pak"
        };

        long totalBytesRead = 0;

        foreach (var file in EnumerateFilesDepthCapped(installDir, "*", ByteScanDepth, logger))
        {
            if (scannedFiles + skippedFiles >= MaxScanFilesPerGame)
            {
                logger.LogDebug("[game-sync-scanner] file budget reached for {Dir}", installDir);
                break;
            }

            if (totalBytesRead >= MaxScanBytesPerGame)
            {
                logger.LogDebug("[game-sync-scanner] byte budget reached for {Dir}", installDir);
                break;
            }

            var ext = Path.GetExtension(file);
            if (!extensions.Contains(ext))
            {
                continue;
            }

            long size;
            try
            {
                size = new FileInfo(file).Length;
            }
            catch (Exception ex)
            {
                logger.LogDebug(ex, "[game-sync-scanner] cannot stat {File}", file);
                continue;
            }

            var maxSize = IsBinaryExtension(ext) ? MaxScanBinarySizeBytes : MaxScanArchiveSizeBytes;
            if (size > maxSize)
            {
                logger.LogDebug("[game-sync-scanner] skipping large file {File} ({Size} bytes)", file, size);
                skippedFiles++;
                continue;
            }

            // Clamp to remaining byte budget so we don't read more than allowed.
            var remaining = MaxScanBytesPerGame - totalBytesRead;
            if (size > remaining)
            {
                logger.LogDebug("[game-sync-scanner] skipping {File}: would exceed byte budget", file);
                skippedFiles++;
                continue;
            }

            try
            {
                if (ContainsChromaStringStreamed(file, size))
                {
                    scannedFiles++;
                    totalBytesRead += size;
                    return true;
                }

                scannedFiles++;
                totalBytesRead += size;
            }
            catch (Exception ex)
            {
                logger.LogDebug(ex, "[game-sync-scanner] cannot read {File}", file);
            }
        }

        return false;
    }

    // Scans a file for Chroma SDK marker strings using a fixed buffer to avoid
    // allocating the full file in the LOH. Handles search terms that may straddle
    // a buffer boundary by retaining a suffix of the previous chunk.
    private static bool ContainsChromaStringStreamed(string path, long fileSize)
    {
        // For small files, ReadAllBytes is already cheap and avoids the overlap logic.
        if (fileSize <= StreamReadBufferSize * 2)
        {
            var bytes = File.ReadAllBytes(path);
            return ContainsChromaString(bytes.AsSpan());
        }

        // Longest search term in bytes (Unicode): max term chars * 2.
        int maxTermLen = 0;
        foreach (var term in ChromaSearchTerms)
        {
            var utf16Len = term.Length * 2;
            if (utf16Len > maxTermLen)
            {
                maxTermLen = utf16Len;
            }
        }

        // Overlap keeps the tail of each chunk so terms straddling a boundary are found.
        var buf = new byte[StreamReadBufferSize + maxTermLen];
        int overlapLen = 0;

        using var fs = File.OpenRead(path);
        while (true)
        {
            int read = ReadFull(fs, buf, overlapLen, StreamReadBufferSize);
            if (read == 0)
            {
                break;
            }

            int windowLen = overlapLen + read;
            if (ContainsChromaString(buf.AsSpan(0, windowLen)))
            {
                return true;
            }

            // Retain last maxTermLen-1 bytes as overlap for the next chunk.
            int newOverlap = Math.Min(maxTermLen - 1, windowLen);
            if (newOverlap > 0)
            {
                Buffer.BlockCopy(buf, windowLen - newOverlap, buf, 0, newOverlap);
            }

            overlapLen = newOverlap;

            if (read < StreamReadBufferSize)
            {
                break;
            }
        }

        return false;
    }

    private static int ReadFull(Stream s, byte[] buf, int offset, int count)
    {
        int total = 0;
        while (total < count)
        {
            int n = s.Read(buf, offset + total, count - total);
            if (n == 0)
            {
                break;
            }

            total += n;
        }

        return total;
    }

    private static bool IsBinaryExtension(string ext)
        => ext.Equals(".exe", StringComparison.OrdinalIgnoreCase)
        || ext.Equals(".dll", StringComparison.OrdinalIgnoreCase);

    private static bool IsBundledChromaFile(string fileName)
    {
        if (fileName.StartsWith("CChromaEditor", StringComparison.OrdinalIgnoreCase) &&
            fileName.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (fileName.StartsWith("RzChroma", StringComparison.OrdinalIgnoreCase) &&
            fileName.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (fileName.StartsWith("RzChromatic", StringComparison.OrdinalIgnoreCase) &&
            fileName.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (fileName.EndsWith(".chroma", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return false;
    }

    private static readonly string[] ChromaSearchTerms = new[]
    {
        "RzChromaSDK",
        "RzChromatic",
        "ChromaSDK",
        "CChromaEditor",
    };

    private static bool ContainsChromaString(ReadOnlySpan<byte> data)
    {
        foreach (var term in ChromaSearchTerms)
        {
            var asciiBytes = Encoding.ASCII.GetBytes(term);
            if (data.IndexOf(asciiBytes.AsSpan()) >= 0)
            {
                return true;
            }

            var utf16Bytes = Encoding.Unicode.GetBytes(term);
            if (data.IndexOf(utf16Bytes.AsSpan()) >= 0)
            {
                return true;
            }
        }

        return false;
    }

    private static IEnumerable<string> EnumerateFilesDepthCapped(
        string dir, string pattern, int maxDepth, ILogger logger)
    {
        IEnumerable<string> files;
        try
        {
            files = Directory.EnumerateFiles(dir, pattern, SearchOption.TopDirectoryOnly);
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "[game-sync-scanner] cannot enumerate files in {Dir}", dir);
            yield break;
        }

        foreach (var file in files)
        {
            yield return file;
        }

        if (maxDepth <= 0)
        {
            yield break;
        }

        IEnumerable<string> subdirs;
        try
        {
            subdirs = Directory.EnumerateDirectories(dir);
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "[game-sync-scanner] cannot enumerate subdirectories in {Dir}", dir);
            yield break;
        }

        foreach (var subdir in subdirs)
        {
            foreach (var file in EnumerateFilesDepthCapped(subdir, pattern, maxDepth - 1, logger))
            {
                yield return file;
            }
        }
    }

}
