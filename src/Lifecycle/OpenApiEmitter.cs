#if DEBUG
using System;
using System.IO;
using System.Threading;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.OpenApi;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.OpenApi;

namespace Nexus.Service.Lifecycle;

// Writes the generated OpenAPI document (the REST-route inventory committed as
// docs/openapi.json) to disk for the --emit-openapi CLI path. Runs after route
// registration and before any hardware start, so the output matches the real
// route surface without booting the daemon.
internal static class OpenApiEmitter
{
    // The single document AddOpenApi() with no name registers is "v1"; the
    // provider is registered keyed by that name in .NET 10, with a non-keyed
    // fallback for forward-compatibility.
    private const string DocumentName = "v1";

    public static void Emit(WebApplication app, string path)
    {
        // WebApplication registers its mapped endpoints into the DI
        // EndpointDataSource (which ApiExplorer/OpenAPI read) only when the
        // request pipeline is built during StartAsync - without it the document
        // has zero paths. Emit mode strips all hosted services and binds an
        // ephemeral port, so this start runs no background work and serves no
        // traffic; it exists solely to materialize the endpoint table.
        app.StartAsync().GetAwaiter().GetResult();
        try
        {
            var provider = app.Services.GetKeyedService<IOpenApiDocumentProvider>(DocumentName)
                ?? app.Services.GetService<IOpenApiDocumentProvider>()
                ?? throw new InvalidOperationException("OpenAPI document provider not registered");

            var document = provider.GetOpenApiDocumentAsync(CancellationToken.None).GetAwaiter().GetResult();

            var full = Path.GetFullPath(path);
            var dir = Path.GetDirectoryName(full);
            if (!string.IsNullOrEmpty(dir))
                Directory.CreateDirectory(dir);

            using var stream = File.Create(full);
            document.SerializeAsJsonAsync(stream, OpenApiSpecVersion.OpenApi3_1, CancellationToken.None)
                .GetAwaiter().GetResult();

            Console.WriteLine($"[nexus-service] wrote OpenAPI document ({document.Paths.Count} paths) to {full}");
        }
        finally
        {
            app.StopAsync().GetAwaiter().GetResult();
        }
    }
}
#endif
