using DiffusionNexus.Installer.SDK.Catalog;
using DiffusionNexus.Installer.SDK.Catalog.Packaging;
using DiffusionNexus.Installer.SDK.Catalog.Updates;

namespace DiffusionNexus.Installer.Core.Updates;

/// <summary>
/// The catalog an install uses, in one line for its log and its result view (spec 7.2):
/// "Catalog v5 (Stable, 51e1684)". Read-only, and read at install start: no refresh happens
/// before an install, so what is installed then is what the install reads.
/// </summary>
public static class InstalledCatalogDescription
{
    public static string Describe(CatalogOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        // The SDK's locator rule: an override folder wins only when it holds a catalog. Then the
        // installed state describes nothing this install reads.
        if (!string.IsNullOrWhiteSpace(options.LocalOverridePath)
            && File.Exists(Path.Combine(options.LocalOverridePath, CatalogSchema.RootFileName)))
            return $"Catalog: local override at {options.LocalOverridePath}";

        return Describe(LocalCatalogState.Load(options.InstalledCatalogPath));
    }

    public static string Describe(LocalCatalogState state)
    {
        ArgumentNullException.ThrowIfNull(state);

        // Workloads first: that section brings the shared files, and the stamp follows it.
        if ((state.Workloads ?? state.Workflows) is not { } main) return "Catalog: none recorded (no catalog-state.json)";

        var text = $"Catalog {Section(main, state.Channel)}";
        if (state.Workloads is { } workloads && state.Workflows is { } workflows
            && Section(workflows, state.Channel) != Section(workloads, state.Channel))
            text += $"; workflows {Section(workflows, state.Channel)}";
        return text;
    }

    // A section's own channel when the state records it (SDK 2.1.0); the stamp otherwise.
    private static string Section(SectionState section, CatalogChannel stamp)
    {
        var commit = section.Commit?.Trim();
        var shortCommit = string.IsNullOrEmpty(commit) ? null : ", " + commit[..Math.Min(7, commit.Length)];
        return $"v{section.CatalogVersion} ({section.Channel ?? stamp}{shortCommit})";
    }
}
