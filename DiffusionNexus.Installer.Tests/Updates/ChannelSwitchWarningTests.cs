using DiffusionNexus.Installer.Core.Updates;
using DiffusionNexus.Installer.SDK.Catalog.Packaging;
using DiffusionNexus.Installer.SDK.Catalog.Updates;
using DiffusionNexus.Installer.Tests.Support;
using FluentAssertions;
using Xunit;

namespace DiffusionNexus.Installer.Tests.Updates;

/// <summary>
/// The warning before a channel switch (spec 7.1): what goes away from the list of things you
/// can install. "Removed" and "Updated" entries count; "Added" ones take nothing away.
/// </summary>
public class ChannelSwitchWarningTests
{
    [Fact]
    public void Names_what_goes_away_and_counts_what_changes()
    {
        var check = CatalogChecks.Available(4, CatalogChannel.Stable,
            workloads: [CatalogChecks.WorkloadRemoved("Qwen-Image-2.1", "V1.0"), CatalogChecks.WorkloadAdded("Wan-2.3", "V1.0")],
            workflows:
            [
                CatalogChecks.WorkflowUpdated("Upscale", "V2", "V1", "Krea-2-Turbo"),
                CatalogChecks.WorkflowUpdated("Inpaint", "V3", "V2", "Krea-2-Turbo"),
                CatalogChecks.WorkflowUpdated("Outpaint", "V3", "V2", "Krea-2-Turbo"),
            ]);

        var warning = ChannelSwitchWarning.For(check);

        warning.Should().NotBeNull();
        warning!.Target.Should().Be(CatalogChannel.Stable);
        warning.Version.Should().Be(4);
        warning.Removed.Should().Equal("Qwen-Image-2.1");
        warning.ChangesText.Should().Be("changes 3 workflows");
    }

    [Fact]
    public void A_removed_workflow_is_named_the_way_the_change_list_names_it()
    {
        var check = new CatalogUpdateCheck(CatalogUpdateOutcome.UpdatesAvailable, CatalogChannel.Stable, CatalogChecks.Remote(4, CatalogChannel.Stable),
            new LocalCatalogState(), [], [new WorkflowChange(Guid.NewGuid(), "Upscale", ["Krea-2-Turbo"], ChangeKind.Removed, "V2", null)], null);

        ChannelSwitchWarning.For(check)!.Removed.Should().Equal("Krea-2-Turbo – Upscale");
        ChannelSwitchWarning.For(check)!.ChangesText.Should().BeNull();
    }

    [Theory]
    [InlineData(1, 0, "changes 1 workload")]
    [InlineData(0, 1, "changes 1 workflow")]
    [InlineData(2, 1, "changes 2 workloads and 1 workflow")]
    public void Counts_read_as_words(int workloads, int workflows, string expected)
    {
        var check = CatalogChecks.Available(4, CatalogChannel.Stable,
            workloads: [.. Enumerable.Range(0, workloads).Select(i => CatalogChecks.WorkloadUpdated($"W{i}", "V2", "V1"))],
            workflows: [.. Enumerable.Range(0, workflows).Select(i => CatalogChecks.WorkflowUpdated($"F{i}", "V2", "V1"))]);

        ChannelSwitchWarning.For(check)!.ChangesText.Should().Be(expected);
    }

    [Fact]
    public void Only_additions_need_no_warning()
    {
        var check = CatalogChecks.Available(6, CatalogChannel.Stable, workloads: [CatalogChecks.WorkloadAdded("Wan-2.3", "V1.0")]);

        ChannelSwitchWarning.For(check).Should().BeNull();
    }

    // A check that differs only in the shared files (2.1.0's sharedHash) lists nothing: nothing
    // the user can name goes away, so the switch applies without asking.
    [Fact]
    public void An_empty_diff_needs_no_warning()
    {
        var check = CatalogChecks.Available(4, CatalogChannel.Stable, workloads: [], workflows: []);

        ChannelSwitchWarning.For(check).Should().BeNull();
    }

    [Theory]
    [InlineData(CatalogUpdateOutcome.UpToDate)]
    [InlineData(CatalogUpdateOutcome.Failed)]
    [InlineData(CatalogUpdateOutcome.RequiresNewerSoftware)]
    [InlineData(CatalogUpdateOutcome.OverrideActive)]
    public void Only_an_available_update_can_warn(CatalogUpdateOutcome outcome)
    {
        var check = CatalogChecks.Available(4, CatalogChannel.Stable, workloads: [CatalogChecks.WorkloadRemoved("Qwen-Image-2.1", "V1.0")]) with { Outcome = outcome };

        ChannelSwitchWarning.For(check).Should().BeNull();
    }
}
