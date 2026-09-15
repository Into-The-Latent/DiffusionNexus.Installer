using DiffusionNexus.Installer.SDK.Models.Configuration;
using DiffusionNexus.Installer.SDK.Models.Enums;

namespace DiffusionNexus.Installer.Core.Catalog;

/// <summary>
/// Which catalogued workloads this installer may offer. Three flags, each answering a different
/// question the catalog author asked:
///
/// <list type="bullet">
/// <item><c>workloadTarget</c> — whose app is this for? DiffusionNexusCore workloads belong to the
/// main app and are never offered here.</item>
/// <item><c>isLegacy</c> — superseded by a newer pack. Hidden in every build: the 1.x wizard hid
/// legacy workloads unconditionally and only the classic list had an opt-in toggle, which this
/// installer has no equivalent of.</item>
/// <item><c>isReleaseConfig</c> — finished, or authored for testing? Draft entries stay visible in
/// local Debug builds (that is what makes them testable at all) and never reach a shipped one.
/// </item>
/// </list>
///
/// A policy object rather than an <c>#if</c> inside the filter, so both halves stay covered by
/// tests that run in the one configuration the test suite is built in.
/// </summary>
public sealed class WorkloadVisibility
{
    private WorkloadVisibility(bool includeNonRelease) => IncludeNonRelease = includeNonRelease;

    /// <summary>What a shipped build offers: released, non-legacy, installer-targeted workloads.</summary>
    public static WorkloadVisibility ReleaseOnly { get; } = new(includeNonRelease: false);

    /// <summary>The above plus draft entries, so a catalog author can exercise one locally.</summary>
    public static WorkloadVisibility IncludingNonRelease { get; } = new(includeNonRelease: true);

    /// <summary>The policy the app registers: relaxed in Debug builds, strict in shipped ones.</summary>
    public static WorkloadVisibility Default { get; } =
#if DEBUG
        IncludingNonRelease;
#else
        ReleaseOnly;
#endif

    public bool IncludeNonRelease { get; }

    public bool IsOffered(InstallationConfiguration workload)
    {
        ArgumentNullException.ThrowIfNull(workload);

        return workload.WorkloadTarget == WorkloadTargetType.Installer
            && !workload.IsLegacy
            && (workload.IsReleaseConfig || IncludeNonRelease);
    }
}
