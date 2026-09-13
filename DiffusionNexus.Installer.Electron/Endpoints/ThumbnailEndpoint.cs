using DiffusionNexus.Installer.Core.Catalog;

namespace DiffusionNexus.Installer.Electron.Endpoints;

/// <summary>
/// Serves workload thumbnails out of the catalog.
///
/// InstallationConfiguration.ThumbnailPath is an ABSOLUTE disk path under the installed catalog,
/// which no browser can load, so the cards cannot point at it directly. This endpoint is keyed by
/// workload id -- never by a caller-supplied path -- so there is no traversal surface.
/// </summary>
public static class ThumbnailEndpoint
{
    public static IEndpointRouteBuilder MapWorkloadThumbnails(this IEndpointRouteBuilder app)
    {
        app.MapGet("/thumbnail/{workloadId:guid}", async (
            Guid workloadId,
            IWorkloadSource workloads,
            HttpResponse response,
            CancellationToken ct) =>
        {
            var bytes = await workloads.GetThumbnailAsync(workloadId, ct);
            if (bytes is null || bytes.Length == 0) return Results.NotFound();

            var all = await workloads.GetInstallerWorkloadsAsync(ct);
            var path = all.FirstOrDefault(w => w.Id == workloadId)?.ThumbnailPath;

            // The catalog is immutable between updates, and 20+ cards would otherwise re-request
            // every thumbnail on each render of the workload screen.
            response.Headers.CacheControl = "private, max-age=3600";

            return Results.File(bytes, ContentTypeFor(path));
        });

        return app;
    }

    /// <summary>
    /// Content type from the thumbnail's own extension. The catalog writer preserves the source
    /// extension, so this is not always webp.
    /// </summary>
    public static string ContentTypeFor(string? thumbnailPath) =>
        Path.GetExtension(thumbnailPath ?? string.Empty).ToLowerInvariant() switch
        {
            ".webp" => "image/webp",
            ".png" => "image/png",
            ".jpg" or ".jpeg" => "image/jpeg",
            ".gif" => "image/gif",
            _ => "application/octet-stream"
        };
}
