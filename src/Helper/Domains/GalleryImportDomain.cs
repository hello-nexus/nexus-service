#if WINDOWS
using System.Runtime.Versioning;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Nexus.Service.Gallery;
using Nexus.Service.Models.Gallery;
using Nexus.Service.Serialization;

namespace Nexus.Service.Helper.Domains;

/// <summary>
/// Service side of gallery.import: the user-session helper sees the user's
/// mapped drives and network credentials, so it does the copy.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class HelperGalleryImporter : IGalleryImporter
{
    public const string ImportType = "gallery.import";
    // A copy of up to MaxImportBytes over a home network.
    private const int TimeoutMs = 900_000;

    private readonly HelperRegistry _registry;

    public HelperGalleryImporter(HelperRegistry registry)
    {
        _registry = registry;
    }

    public async Task<GalleryImportResult> ImportAsync(GalleryImportRequest request, CancellationToken ct)
    {
        var conn = _registry.GetForConsoleOrAny();
        if (conn is null)
        {
            return new GalleryImportResult { Error = "no interactive user session", Code = GalleryErrorCodes.Unreachable };
        }

        var result = await conn.SendCommandAsync(
            ImportType,
            request,
            AppJsonContext.Default.GalleryImportRequest,
            timeoutMs: TimeoutMs,
            ct: ct).ConfigureAwait(false);

        if (!result.Ok || result.Payload is null)
        {
            return new GalleryImportResult { Error = result.Error ?? "import failed", Code = GalleryErrorCodes.ImportFailed };
        }

        try
        {
            return JsonSerializer.Deserialize(result.Payload.Value, AppJsonContext.Default.GalleryImportResult)
                ?? new GalleryImportResult { Error = "bad payload", Code = GalleryErrorCodes.ImportFailed };
        }
        catch
        {
            return new GalleryImportResult { Error = "bad payload", Code = GalleryErrorCodes.ImportFailed };
        }
    }
}

[SupportedOSPlatform("windows")]
public sealed class GalleryImportHandler
{
    public void Register(HelperHandlerRegistry registry)
    {
        registry.Register(HelperGalleryImporter.ImportType, async (env, ct) =>
        {
            var req = env.Payload is null
                ? new GalleryImportRequest()
                : JsonSerializer.Deserialize(env.Payload.Value, AppJsonContext.Default.GalleryImportRequest) ?? new GalleryImportRequest();
            var result = await Task.Run(() => GalleryImportCopier.Copy(req, ct), ct).ConfigureAwait(false);
            return new HelperResult
            {
                Id = env.Id ?? "",
                Ok = true,
                Payload = JsonSerializer.SerializeToElement(result, AppJsonContext.Default.GalleryImportResult),
            };
        });
    }
}
#endif
