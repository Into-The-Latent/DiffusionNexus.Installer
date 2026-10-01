using DiffusionNexus.Installer.SDK.Catalog.Packaging;
using DiffusionNexus.Installer.SDK.Catalog.Updates;

namespace DiffusionNexus.Installer.Core.Updates;

/// <summary>
/// What a channel switch takes away from the list of things you can install, shown before the
/// switch (spec 7.1): the removed entries by name, the changed ones counted. Added entries take
/// nothing away and never warn.
/// </summary>
public sealed record ChannelSwitchWarning(
    CatalogChannel Target,
    int Version,
    IReadOnlyList<string> Removed,
    int WorkloadsChanged,
    int WorkflowsChanged)
{
    /// <summary>The warning for a target-channel check, or null when the switch takes nothing away.</summary>
    public static ChannelSwitchWarning? For(CatalogUpdateCheck check)
    {
        ArgumentNullException.ThrowIfNull(check);
        if (check.Outcome != CatalogUpdateOutcome.UpdatesAvailable) return null;

        // Named as the change list below the warning names them, so the two read as one.
        var removed = CatalogChangeRows.Build(check).Where(r => r.Change == ChangeKind.Removed).Select(r => r.Name).ToList();
        var workloads = check.Workloads.Count(w => w.Kind == ChangeKind.Updated);
        var workflows = check.Workflows.Count(w => w.Kind == ChangeKind.Updated);

        return removed.Count == 0 && workloads + workflows == 0
            ? null
            : new ChannelSwitchWarning(check.Channel, check.Remote?.CatalogVersion ?? 0, removed, workloads, workflows);
    }

    /// <summary>"changes 2 workloads and 1 workflow", or null when nothing changes.</summary>
    public string? ChangesText
    {
        get
        {
            var counts = new[] { Count(WorkloadsChanged, "workload"), Count(WorkflowsChanged, "workflow") }.OfType<string>().ToList();
            return counts.Count == 0 ? null : "changes " + string.Join(" and ", counts);
        }
    }

    private static string? Count(int n, string noun) => n switch
    {
        0 => null,
        1 => $"1 {noun}",
        _ => $"{n} {noun}s",
    };
}

/// <summary>A switch the user has been warned about and not answered yet.</summary>
/// <param name="Preview">The check of the target channel; Switch applies exactly this.</param>
public sealed record CatalogChannelSwitch(CatalogUpdateCheck Preview, ChannelSwitchWarning Warning)
{
    public CatalogChannel Target => Preview.Channel;
}
