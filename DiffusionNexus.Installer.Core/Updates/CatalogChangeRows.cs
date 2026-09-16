using DiffusionNexus.Installer.SDK.Catalog.Updates;

namespace DiffusionNexus.Installer.Core.Updates;

/// <summary>One line of the "what changed" list. Kind is "Workload" or "Workflow".</summary>
public sealed record CatalogChangeRow(string Kind, string Name, ChangeKind Change, string VersionText);

/// <summary>
/// Flattens a check into the rows /updates renders, in the shape the catalog editor's Release
/// dialog shows the author: grouped Added, Updated, Removed; "from → to" for updates; workflows
/// named "Workload – Workflow". What the author approved is what the user reads.
/// </summary>
public static class CatalogChangeRows
{
    public static IReadOnlyList<CatalogChangeRow> Build(CatalogUpdateCheck check)
    {
        ArgumentNullException.ThrowIfNull(check);

        var rows = new List<CatalogChangeRow>(check.Workloads.Count + check.Workflows.Count);
        foreach (var w in check.Workloads)
            rows.Add(new CatalogChangeRow("Workload", w.Name, w.Kind, VersionText(w.Kind, w.FromVersion, w.ToVersion)));
        foreach (var w in check.Workflows)
            rows.Add(new CatalogChangeRow("Workflow", WorkflowName(w), w.Kind, VersionText(w.Kind, w.FromVersion, w.ToVersion)));

        // OrderBy is stable, so input order (workloads first, then workflows) survives inside each group.
        return rows.OrderBy(r => GroupOrder(r.Change)).ToList();
    }

    private static int GroupOrder(ChangeKind kind) => kind switch
    {
        ChangeKind.Added => 0,
        ChangeKind.Updated => 1,
        _ => 2,
    };

    private static string WorkflowName(WorkflowChange w) =>
        w.WorkloadNames.Count == 0 ? w.Name : $"{string.Join(", ", w.WorkloadNames)} – {w.Name}";

    private static string VersionText(ChangeKind kind, string? from, string? to) => kind switch
    {
        ChangeKind.Added => to ?? string.Empty,
        ChangeKind.Removed => from ?? string.Empty,
        _ => $"{from} → {to}",
    };
}
