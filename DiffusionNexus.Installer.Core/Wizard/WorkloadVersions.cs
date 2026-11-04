using DiffusionNexus.Installer.SDK.Models.Configuration;

namespace DiffusionNexus.Installer.Core.Wizard;

/// <summary>A workload's versions, by the memory they need, for Select multiple.</summary>
public static class WorkloadVersions
{
    public static IReadOnlyList<int> Tiers(InstallationConfiguration workload)
    {
        ArgumentNullException.ThrowIfNull(workload);
        return VramTiers.Parse(workload.Vram.VramProfiles);
    }

    /// <summary>
    /// The largest version that fits. When none does, the smallest: a card never greys out for memory
    /// reasons, and the link filter would otherwise leave a workload with only its VAE and text encoder.
    /// </summary>
    /// <param name="tiers">The workload's versions, ascending.</param>
    /// <param name="memoryGb">The memory the user set.</param>
    /// <returns>The version in GB, or 0 for a workload that declares none.</returns>
    public static int ForMemory(IReadOnlyList<int> tiers, int memoryGb)
    {
        ArgumentNullException.ThrowIfNull(tiers);
        if (tiers.Count == 0) return 0;

        var fitting = tiers.Where(t => t <= memoryGb).ToList();
        return fitting.Count > 0 ? fitting[^1] : tiers[0];
    }

    /// <summary>The red note under a card. Null when the version fits.</summary>
    public static string? TooLargeNote(int version, int memoryGb) =>
        version > memoryGb ? $"This version needs {version} GB, so it may not run on your {memoryGb} GB card." : null;
}
