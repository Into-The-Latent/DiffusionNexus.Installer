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
        // A software whose every pack is legacy would read "0 workloads" and open onto a screen
        // that stays empty until the user finds the legacy switch. It gets no card, and so no
        // workload screen either -- its packs are out of reach (see SoftwareEntry.Workloads).
        .Where(s => s.WorkloadCount > 0)
        // Most-offering first, counted over current workloads only (legacy ones sit behind the
        // workload screen's switch): ComfyUI carries most of what this installer offers, so it
        // belongs at the top rather than wherever the catalog's own order happens to put it.
        .OrderByDescending(s => s.WorkloadCount)
        .ThenBy(s => s.DisplayName, StringComparer.CurrentCultureIgnoreCase)
        .ToList();
}
