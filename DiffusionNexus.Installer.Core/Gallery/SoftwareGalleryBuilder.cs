namespace DiffusionNexus.Installer.Core.Gallery;

/// <summary>
/// Turns the flat workload gallery into the software cards the welcome screen shows.
///
/// Catalog-derived, like the filters it replaces: a software the catalog does not contain never
/// gets a card, and one it gains later appears without a code change.
/// </summary>
public sealed class SoftwareGalleryBuilder(GalleryBuilder inner)
{
    public async Task<IReadOnlyList<SoftwareEntry>> BuildAsync(CancellationToken ct = default) =>
        Group(await inner.BuildAsync(ct));

    /// <summary>Pure grouping, separated from the catalog read so it can be tested without one.</summary>
    public static IReadOnlyList<SoftwareEntry> Group(IEnumerable<GalleryEntry> entries) => entries
        .GroupBy(e => e.Workload.Repository.Type)
        .Select(g => new SoftwareEntry(
            g.Key,
            SoftwareBranding.DisplayName(g.Key),
            g.ToList()))
        // Most-offering first: ComfyUI carries 16 of the 21 workloads this installer can offer
        // (the catalog holds 25, but four target DiffusionNexusCore and never reach here), so it
        // belongs at the top rather than wherever the catalog's own order happens to put it.
        .OrderByDescending(s => s.WorkloadCount)
        .ThenBy(s => s.DisplayName, StringComparer.CurrentCultureIgnoreCase)
        .ToList();
}
