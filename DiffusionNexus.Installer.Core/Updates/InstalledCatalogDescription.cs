using DiffusionNexus.Installer.SDK.Catalog;
using DiffusionNexus.Installer.SDK.Catalog.Packaging;
using DiffusionNexus.Installer.SDK.Catalog.Updates;

namespace DiffusionNexus.Installer.Core.Updates;

/// <summary>
/// The one place installed-catalog provenance is put into words: the install report line
/// (spec 7.2), the /updates Catalog row and the "still from" line after a switch. One rule for
/// all three: a section's channel is what that section records (SDK 2.1.0), and a section that
/// records none has no known channel. The state's top-level stamp is never used for it: SDK
/// 2.0.0 stamped channels over content that never landed. The "still from" line also weighs what
/// the installer knows beyond the state (<see cref="CatalogSourceEvidence"/>).
/// </summary>
public static class InstalledCatalogDescription
{
    /// <summary>
    /// "Catalog v5 (Stable, 51e1684)". <paramref name="loaded"/> is the state of the catalog load
    /// the caller read its workloads from (<c>ICatalog.State</c>), described as it is: a second read
    /// of the file could describe an apply that landed after that load (PR #43 review). Without one,
    /// or when it records nothing, the file is read. Throws when the file exists but cannot be read
    /// right now.
    /// </summary>
    /// <exception cref="IOException">catalog-state.json is held by another process.</exception>
    /// <exception cref="UnauthorizedAccessException">catalog-state.json may not be read.</exception>
    public static string Describe(CatalogOptions options, LocalCatalogState? loaded = null)
    {
        ArgumentNullException.ThrowIfNull(options);

        // The SDK's locator rule: an override folder wins only when it holds a catalog. Then the
        // installed state describes nothing this install reads.
        if (OverrideActive(options)) return $"Catalog: local override at {options.LocalOverridePath}";

        // ICatalog reads its state tolerantly, so an empty one may be a file that was held during
        // the load. Read, not Load: Load answers "no state" for a locked file, and the report would
        // then state something false instead of saying it could not look.
        return Describe(loaded is { Workloads: not null } or { Workflows: not null }
            ? loaded
            : LocalCatalogState.Read(options.InstalledCatalogPath));
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
    /// null when nothing is known to be: "The installed catalog is still from Preview (v5)." Judged
    /// per section (PR #43 review). A section is from the channel it records, or from the channel
    /// a check found it current on; found current on the followed channel, it is not lagging
    /// whatever it records. A section known by neither is lagging only after this run's switch to
    /// the followed channel did not land, and then nothing is claimed about its source: "The
    /// installed workflows (v5) are not from Stable yet." Unknown alone is never a mismatch.
    /// </summary>
    public static string? Lagging(LocalCatalogState state, CatalogChannel followed, CatalogSourceEvidence? evidence = null)
    {
        ArgumentNullException.ThrowIfNull(state);
        evidence ??= CatalogSourceEvidence.None;

        var workloads = Judge(state.Workloads, c => c.Workloads, followed, evidence);
        var workflows = Judge(state.Workflows, c => c.Workflows, followed, evidence);

        var known = (workloads.From, workflows.From) switch
        {
            (null, null) => null,
            ({ } a, { } b) when a == b => $"The installed catalog is still from {a}.",
            ({ } a, { } b) => $"The installed workloads are still from {a}, the workflows from {b}.",
            ({ } a, null) => $"The installed workloads are still from {a}.",
            (null, { } b) => $"The installed workflows are still from {b}.",
        };
        var unknown = (workloads.NotLanded, workflows.NotLanded) switch
        {
            (null, null) => null,
            ({ } a, { } b) when a.CatalogVersion == b.CatalogVersion => $"The installed catalog (v{a.CatalogVersion}) is not from {followed} yet.",
            ({ } a, { } b) => $"The installed workloads (v{a.CatalogVersion}) and workflows (v{b.CatalogVersion}) are not from {followed} yet.",
            ({ } a, null) => $"The installed workloads (v{a.CatalogVersion}) are not from {followed} yet.",
            (null, { } b) => $"The installed workflows (v{b.CatalogVersion}) are not from {followed} yet.",
        };
        return known is null ? unknown : unknown is null ? known : known + " " + unknown;
    }

    /// <summary>A lagging section: From when its source is known ("Preview (v5)"), NotLanded when only this run's failed switch is.</summary>
    private static (string? From, SectionState? NotLanded) Judge(
        SectionState? section, Func<ConfirmedSections, SectionState?> pick, CatalogChannel followed, CatalogSourceEvidence evidence)
    {
        if (section is null) return default;
        if (evidence.Confirmed.TryGetValue(followed, out var current) && pick(current) == section) return default;

        var source = section.Channel
            ?? evidence.Confirmed.Where(kv => kv.Key != followed && pick(kv.Value) == section).Select(kv => (CatalogChannel?)kv.Key).FirstOrDefault();
        if (source is { } channel) return channel == followed ? default : ($"{channel} (v{section.CatalogVersion})", null);
        return evidence.NotLandedTo == followed ? (null, section) : default;
    }

    /// <summary>The SDK's locator rule for <see cref="CatalogOptions.LocalOverridePath"/>.</summary>
    public static bool OverrideActive(CatalogOptions options) =>
        !string.IsNullOrWhiteSpace(options.LocalOverridePath)
        && File.Exists(Path.Combine(options.LocalOverridePath, CatalogSchema.RootFileName));

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

/// <summary>What the installer knows about installed content beyond what catalog-state.json records.</summary>
public sealed record CatalogSourceEvidence
{
    public static readonly CatalogSourceEvidence None = new();

    /// <summary>Each channel a check found the installed content current on (<see cref="CatalogChannelConfirmations"/>).</summary>
    public IReadOnlyDictionary<CatalogChannel, ConfirmedSections> Confirmed { get; init; } = new Dictionary<CatalogChannel, ConfirmedSections>();

    /// <summary>This run's switch whose content did not land, for sections known by nothing else.</summary>
    public CatalogChannel? NotLandedTo { get; init; }
}

/// <summary>Reads the installed catalog's provenance line. Singleton; never caches.</summary>
public interface ICatalogProvenance
{
    /// <summary>Reads catalog-state.json. Never throws.</summary>
    InstalledCatalogReading Read();

    /// <summary>
    /// Runs <paramref name="read"/> (a catalog read, the wizard's workloads) and describes the state
    /// of the same catalog load, taking the read again when the catalog reloaded under it. Only
    /// <paramref name="read"/>'s own exceptions propagate.
    /// </summary>
    Task<(T Value, InstalledCatalogReading Catalog)> ReadWithAsync<T>(Func<CancellationToken, Task<T>> read, CancellationToken ct = default);
}

public sealed class CatalogProvenance(CatalogOptions options, ICatalog catalog) : ICatalogProvenance
{
    private const int Attempts = 3;

    public InstalledCatalogReading Read() => InstalledCatalogReading.Take(() => InstalledCatalogDescription.Describe(options));

    public async Task<(T Value, InstalledCatalogReading Catalog)> ReadWithAsync<T>(Func<CancellationToken, Task<T>> read, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(read);
        for (var attempt = 1; ; attempt++)
        {
            // ICatalog.State is the cached state of the current load, the same instance until the
            // catalog reloads (an apply invalidates it). The same instance before and after the read
            // means the read came from that load. Off the caller's thread: State blocks while a load runs.
            var before = await StateAsync(ct).ConfigureAwait(false);
            var value = await read(ct).ConfigureAwait(false);
            var after = await StateAsync(ct).ConfigureAwait(false);
            if (before is not null && ReferenceEquals(before, after))
                return (value, InstalledCatalogReading.Take(() => InstalledCatalogDescription.Describe(options, after)));

            // A failed load, or a catalog that keeps reloading (an apply mid-swap): the file is the
            // best description left.
            if (before is null || after is null || attempt == Attempts) return (value, Read());
        }
    }

    /// <summary>Null when the load failed: the reading then falls back to the file, never fails the caller's read.</summary>
    private async Task<LocalCatalogState?> StateAsync(CancellationToken ct)
    {
        try
        {
            return await Task.Run(() => catalog.State, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return null;
        }
    }
}
