using DiffusionNexus.Installer.Core.Catalog;
using Microsoft.AspNetCore.Diagnostics;

namespace DiffusionNexus.Installer.Electron.Endpoints;

/// <summary>
/// Serves workload thumbnails out of the catalog.
///
/// InstallationConfiguration.ThumbnailPath is an ABSOLUTE disk path under the installed catalog,
/// which no browser can load, so the cards cannot point at it directly. This endpoint is keyed by
/// workload id -- never by a caller-supplied path -- so there is no traversal surface.
///
/// The GetInstallerWorkloadAsync lookup below is the authorization check, not an extension sniff:
/// IWorkloadSource.GetThumbnailAsync reads straight through to the catalog with no WorkloadTarget
/// filter, so it will happily return bytes for a DiffusionNexusCore workload, which this installer
/// never offers. The lookup must run -- and gate -- before any bytes are read.
/// </summary>
public static class ThumbnailEndpoint
{
    public static IEndpointRouteBuilder MapWorkloadThumbnails(this IEndpointRouteBuilder app)
    {
        app.MapGet("/thumbnail/{workloadId:guid}", HandleAsync);

        return app;
    }

    public static async Task<IResult> HandleAsync(
        Guid workloadId, IWorkloadSource workloads, HttpResponse response, CancellationToken ct)
    {
        // The workload lookup is the authorization check, not an extension sniff:
        // GetThumbnailAsync reads straight through to the catalog with no WorkloadTarget
        // filter, so without this a DiffusionNexusCore workload -- which this installer never
        // offers -- would serve its artwork here with a 200.
        //
        // By id, never the whole list: the list member hands back a deep copy of all 25 catalogued
        // workloads, nested model-download lists included, and a 16-card grid asks this question
        // 16 times per render.
        var workload = await workloads.GetInstallerWorkloadAsync(workloadId, ct);
        if (workload is null) return NotFound(response);

        var bytes = await workloads.GetThumbnailAsync(workloadId, ct);
        if (bytes is null || bytes.Length == 0) return NotFound(response);

        // The catalog is immutable between updates, and 20+ cards would otherwise re-request
        // every thumbnail on each render of the workload screen.
        response.Headers.CacheControl = "private, max-age=3600";

        return Results.File(bytes, ContentTypeFor(workload.ThumbnailPath));
    }

    /// <summary>
    /// A 404 that stays a 404.
    ///
    /// Program.cs installs UseStatusCodePagesWithReExecute("/not-found") ahead of every endpoint,
    /// and it re-executes the whole pipeline for any 400-599 response with an empty body and no
    /// content type. A bare Results.NotFound() from here therefore routes into Pages.NotFound and
    /// serves a complete text/html Blazor document -- rendered through an InteractiveServer
    /// component -- as the body of an &lt;img&gt; request. The status survives, so the card's alt
    /// text still appears; what does not survive is the cost, once per broken tile per render.
    ///
    /// Disabling the feature for this request is the framework's own documented opt-out, and it is
    /// per-request: nothing else loses its error page.
    /// </summary>
    private static IResult NotFound(HttpResponse response)
    {
        var statusCodePages = response.HttpContext.Features.Get<IStatusCodePagesFeature>();
        if (statusCodePages is not null) statusCodePages.Enabled = false;

        return Results.NotFound();
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
