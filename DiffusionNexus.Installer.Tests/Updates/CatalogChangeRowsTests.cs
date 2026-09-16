using DiffusionNexus.Installer.Core.Updates;
using DiffusionNexus.Installer.SDK.Catalog.Updates;
using DiffusionNexus.Installer.Tests.Support;
using FluentAssertions;
using Xunit;

namespace DiffusionNexus.Installer.Tests.Updates;

/// <summary>
/// The rows /updates shows are the rows the editor's Release dialog showed the author: same
/// grouping, same "from → to" text. Anything else and "this is what users will see" is a lie.
/// </summary>
public class CatalogChangeRowsTests
{
    [Fact]
    public void Groups_added_then_updated_then_removed_keeping_input_order_within_a_group()
    {
        var check = CatalogChecks.Available(
            workloads:
            [
                CatalogChecks.WorkloadRemoved("Old", "V1.0"),
                CatalogChecks.WorkloadUpdated("Krea-2-Turbo", "V1.0", "V1.1"),
                CatalogChecks.WorkloadAdded("Ernie-Image-Turbo", "V1.0"),
                CatalogChecks.WorkloadUpdated("Wan 2.2", "V2.0", "V2.1"),
            ]);

        var rows = CatalogChangeRows.Build(check);

        rows.Select(r => (r.Change, r.Name)).Should().Equal(
            (ChangeKind.Added, "Ernie-Image-Turbo"),
            (ChangeKind.Updated, "Krea-2-Turbo"),
            (ChangeKind.Updated, "Wan 2.2"),
            (ChangeKind.Removed, "Old"));
    }

    [Fact]
    public void Version_text_depends_on_the_kind_of_change()
    {
        var check = CatalogChecks.Available(
            workloads:
            [
                CatalogChecks.WorkloadAdded("A", "V1.0"),
                CatalogChecks.WorkloadUpdated("B", "V1.0", "V1.1"),
                CatalogChecks.WorkloadRemoved("C", "V3.0"),
            ]);

        CatalogChangeRows.Build(check).Select(r => r.VersionText).Should().Equal("V1.0", "V1.0 → V1.1", "V3.0");
    }

    [Fact]
    public void Workflows_are_named_after_their_workload_and_follow_workloads_in_a_group()
    {
        var check = CatalogChecks.Available(
            workloads: [CatalogChecks.WorkloadAdded("Wan 2.2", "V1.0")],
            workflows: [CatalogChecks.WorkflowAdded("Text to Video", "V1.0", "Wan 2.2")]);

        var rows = CatalogChangeRows.Build(check);

        rows.Should().HaveCount(2);
        rows[0].Kind.Should().Be("Workload");
        rows[1].Should().Be(new CatalogChangeRow("Workflow", "Wan 2.2 – Text to Video", ChangeKind.Added, "V1.0"));
    }

    [Fact]
    public void A_workflow_with_no_workload_keeps_its_own_name()
    {
        var check = CatalogChecks.Available(workloads: [], workflows: [CatalogChecks.WorkflowAdded("Orphan", "V1.0")]);

        CatalogChangeRows.Build(check).Single().Name.Should().Be("Orphan");
    }

    [Fact]
    public void A_workflow_shared_by_several_workloads_lists_them_all()
    {
        var check = CatalogChecks.Available(workloads: [],
            workflows: [CatalogChecks.WorkflowUpdated("Upscale", "V1.0", "V1.1", "Wan 2.2", "LTX-2")]);

        CatalogChangeRows.Build(check).Single().Name.Should().Be("Wan 2.2, LTX-2 – Upscale");
    }

    [Fact]
    public void No_changes_means_no_rows()
    {
        CatalogChangeRows.Build(CatalogChecks.Outcome(CatalogUpdateOutcome.UpToDate)).Should().BeEmpty();
    }
}
