using DiffusionNexus.Installer.SDK.Catalog;
using DiffusionNexus.Installer.SDK.Catalog.Packaging;
using DiffusionNexus.Installer.SDK.Catalog.Updates;

namespace DiffusionNexus.Installer.Core.Updates;

/// <summary>
/// The one place installed-catalog provenance is put into words: the install report line
/// (spec 7.2), the /updates Catalog row and the "still from" line after a switch. One rule for
/// all three: a section's channel is what that section records (SDK 2.1.0), and a section that
/// records none has no known channel. The state's top-level stamp is never used for it: SDK
/// 2.0.0 stamped channels over content that never landed.
/// </summary>
public static class InstalledCatalogDescription
{
    /// <summary>"Catalog v5 (Stable, 51e1684)". Throws when the state file exists but cannot be read right now.</summary>
    /// <exception cref="IOException">catalog-state.json is held by another process.</exception>
    /// <exception cref="UnauthorizedAccessException">catalog-state.json may not be read.</exception>
    public static string Describe(CatalogOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        // The SDK's locator rule: an override folder wins only when it holds a catalog. Then the
        // installed state describes nothing this install reads.
        if (OverrideActive(options)) return $"Catalog: local override at {options.LocalOverridePath}";

        // Read, not Load: Load answers "no state" for a locked file, and the report would then
        // state something false instead of saying it could not look.
        return Describe(LocalCatalogState.Read(options.InstalledCatalogPath));
    }

    public static string Describe(LocalCatalogState state)
    {
        ArgumentNullException.ThrowIfNull(state);

        // Workloads first: that section brings the shared files.
        if ((state.Workloads ?? state.Workflows) is not { } main) return "Catalog: none recorded (no catalog-state.json)";

        var text = $"Catalog {Section(main)}";
        if (state.Workloads is { } workloads && state.Workflows is { } workflows && Section(workflows) != Section(workloads))
            text += $"; workflows {Section(workflows)}";
        return text;
    }

    /// <summary>"v5 (Stable)", or "v5" when the Workloads section records no channel. For the /updates Catalog row.</summary>
    public static string Short(LocalCatalogState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        var main = state.Workloads ?? state.Workflows;
        if (main is null) return "not yet installed";
        return main.Channel is { } channel ? $"v{main.CatalogVersion} ({channel})" : $"v{main.CatalogVersion}";
    }

    /// <summary>
    /// What is installed from a channel other than <paramref name="followed"/>, as a sentence, or
    /// null when nothing is known to be: "The installed catalog is still from Preview (v5)." A
    /// section that records no channel is never counted -- unknown is not a mismatch.
    /// </summary>
    public static string? Lagging(LocalCatalogState state, CatalogChannel followed)
    {
        ArgumentNullException.ThrowIfNull(state);

        var workloads = state.Workloads is { Channel: { } wl } w && wl != followed ? w : null;
        var workflows = state.Workflows is { Channel: { } wf } f && wf != followed ? f : null;

        return (workloads, workflows) switch
        {
            (null, null) => null,
            ({ } a, { } b) when From(a) == From(b) => $"The installed catalog is still from {From(a)}.",
            ({ } a, { } b) => $"The installed workloads are still from {From(a)}, the workflows from {From(b)}.",
            ({ } a, null) => $"The installed workloads are still from {From(a)}.",
            (null, { } b) => $"The installed workflows are still from {From(b)}.",
        };
    }

    /// <summary>
    /// For a state whose sections record no channel, after a switch to <paramref name="followed"/>
    /// did not land: "The installed catalog (v6) is not from Stable yet." Claims no source channel.
    /// </summary>
    public static string NotYetFrom(LocalCatalogState state, CatalogChannel followed)
    {
        ArgumentNullException.ThrowIfNull(state);
        return $"The installed catalog (v{(state.Workloads ?? state.Workflows)?.CatalogVersion ?? 0}) is not from {followed} yet.";
    }

    /// <summary>Whether any section records the channel it came from (SDK 2.1.0 and later).</summary>
    public static bool RecordsChannel(LocalCatalogState state) =>
        state.Workloads?.Channel is not null || state.Workflows?.Channel is not null;

    /// <summary>The SDK's locator rule for <see cref="CatalogOptions.LocalOverridePath"/>.</summary>
    public static bool OverrideActive(CatalogOptions options) =>
        !string.IsNullOrWhiteSpace(options.LocalOverridePath)
        && File.Exists(Path.Combine(options.LocalOverridePath, CatalogSchema.RootFileName));

    private static string From(SectionState section) => $"{section.Channel} (v{section.CatalogVersion})";

    private static string Section(SectionState section)
    {
        var commit = section.Commit?.Trim();
        var shortCommit = string.IsNullOrEmpty(commit) ? null : ", " + commit[..Math.Min(7, commit.Length)];
        return $"v{section.CatalogVersion} ({section.Channel?.ToString() ?? "channel not recorded"}{shortCommit})";
    }
}

/// <summary>
/// The catalog line an install records (spec 7.2), taken once. <see cref="Failed"/>: the state
/// could not be read, and <see cref="Text"/> says so -- a warning row, never a reason not to install.
/// </summary>
public sealed record InstalledCatalogReading(string Text, bool Failed)
{
    /// <summary>Never throws.</summary>
    public static InstalledCatalogReading Take(Func<string> describe)
    {
        ArgumentNullException.ThrowIfNull(describe);
        try
        {
            return new InstalledCatalogReading(describe(), false);
        }
        catch (Exception ex)
        {
            return new InstalledCatalogReading($"Catalog: could not be read: {ex.Message}", true);
        }
    }
}

/// <summary>Reads the installed catalog's provenance line. Singleton; never caches.</summary>
public interface ICatalogProvenance
{
    /// <summary>Never throws.</summary>
    InstalledCatalogReading Read();
}

public sealed class CatalogProvenance(CatalogOptions options) : ICatalogProvenance
{
    public InstalledCatalogReading Read() => InstalledCatalogReading.Take(() => InstalledCatalogDescription.Describe(options));
}
