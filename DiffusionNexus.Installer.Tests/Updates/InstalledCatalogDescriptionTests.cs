using DiffusionNexus.Installer.Core.Updates;
using DiffusionNexus.Installer.SDK.Catalog;
using DiffusionNexus.Installer.SDK.Catalog.Packaging;
using DiffusionNexus.Installer.SDK.Catalog.Updates;
using FluentAssertions;
using Xunit;

namespace DiffusionNexus.Installer.Tests.Updates;

/// <summary>Spec 7.2: the one line an install records about its catalog, for support questions.</summary>
public sealed class InstalledCatalogDescriptionTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"dn-catalog-description-{Guid.NewGuid():N}");
    private static readonly DateTimeOffset Applied = new(2026, 10, 1, 12, 0, 0, TimeSpan.Zero);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (DirectoryNotFoundException) { } catch (IOException) { }
    }

    private static SectionState Section(int version, CatalogChannel? channel, string? commit) =>
        new(version, commit, Applied) { Channel = channel };

    [Fact]
    public void Names_the_version_channel_and_short_commit()
    {
        var state = new LocalCatalogState
        {
            Channel = CatalogChannel.Stable,
            Workloads = Section(5, CatalogChannel.Stable, "51e1684c0ffee1234567"),
            Workflows = Section(5, CatalogChannel.Stable, "51e1684c0ffee1234567"),
        };

        InstalledCatalogDescription.Describe(state).Should().Be("Catalog v5 (Stable, 51e1684)");
    }

    [Fact]
    public void Workflows_from_somewhere_else_are_named_too()
    {
        var state = new LocalCatalogState
        {
            Channel = CatalogChannel.Stable,
            Workloads = Section(5, CatalogChannel.Stable, "51e1684"),
            Workflows = Section(6, CatalogChannel.Preview, "abc1234"),
        };

        InstalledCatalogDescription.Describe(state).Should().Be("Catalog v5 (Stable, 51e1684); workflows v6 (Preview, abc1234)");
    }

    // PR #43 review: a state written by SDK 2.0.0 records no channel per section, and its stamp
    // can name a channel whose content never landed. Unknown is said, never guessed.
    [Fact]
    public void A_section_without_a_channel_says_the_channel_is_not_recorded()
    {
        var state = new LocalCatalogState { Channel = CatalogChannel.Preview, Workloads = Section(5, null, null) };

        InstalledCatalogDescription.Describe(state).Should().Be("Catalog v5 (channel not recorded)");
    }

    // PR #43 review (major): Load answers "no state" for a locked file, so the report claimed
    // "none recorded" with a Success outcome. Read throws, and the reading says it could not look.
    [Fact]
    public void A_state_file_held_by_another_process_is_a_failed_reading_not_no_state()
    {
        var installed = Path.Combine(_dir, "catalog");
        new LocalCatalogState { Workloads = Section(5, CatalogChannel.Stable, "51e1684") }.Save(installed);
        var options = new CatalogOptions { InstalledCatalogPath = installed };

        using (new FileStream(Path.Combine(installed, CatalogSchema.StateFileName), FileMode.Open, FileAccess.Read, FileShare.None))
        {
            var describe = () => InstalledCatalogDescription.Describe(options);
            describe.Should().Throw<IOException>();

            var reading = new CatalogProvenance(options).Read();
            reading.Failed.Should().BeTrue();
            reading.Text.Should().StartWith("Catalog: could not be read: ");
        }

        new CatalogProvenance(options).Read().Should().Be(new InstalledCatalogReading("Catalog v5 (Stable, 51e1684)", false));
    }

    [Fact]
    public void The_row_names_the_workloads_version_and_channel_or_just_the_version()
    {
        InstalledCatalogDescription.Short(new LocalCatalogState { Workloads = Section(5, CatalogChannel.Stable, "a"), Workflows = Section(6, CatalogChannel.Preview, "b") })
            .Should().Be("v5 (Stable)");
        InstalledCatalogDescription.Short(new LocalCatalogState { Channel = CatalogChannel.Preview, Workloads = Section(5, null, "a") })
            .Should().Be("v5");
        InstalledCatalogDescription.Short(new LocalCatalogState()).Should().Be("not yet installed");
    }

    [Fact]
    public void Lagging_names_what_is_installed_from_another_channel()
    {
        var both = new LocalCatalogState { Workloads = Section(5, CatalogChannel.Preview, "a"), Workflows = Section(5, CatalogChannel.Preview, "a") };
        InstalledCatalogDescription.Lagging(both, CatalogChannel.Stable).Should().Be("The installed catalog is still from Preview (v5).");
        InstalledCatalogDescription.Lagging(both, CatalogChannel.Preview).Should().BeNull();
    }

    // PR #43 review: after a partial apply the line named one section's channel and the other's
    // version. It names the section that lagged.
    [Fact]
    public void Lagging_after_a_partial_apply_names_the_section_left_behind()
    {
        var partial = new LocalCatalogState { Workloads = Section(4, CatalogChannel.Stable, "a"), Workflows = Section(5, CatalogChannel.Preview, "b") };

        InstalledCatalogDescription.Lagging(partial, CatalogChannel.Stable).Should().Be("The installed workflows are still from Preview (v5).");
        InstalledCatalogDescription.Lagging(partial, CatalogChannel.Preview).Should().Be("The installed workloads are still from Stable (v4).");
    }

    [Fact]
    public void Lagging_with_two_different_sources_names_both()
    {
        var state = new LocalCatalogState { Workloads = Section(6, CatalogChannel.Preview, "a"), Workflows = Section(5, CatalogChannel.Preview, "b") };

        InstalledCatalogDescription.Lagging(state, CatalogChannel.Stable).Should().Be("The installed workloads are still from Preview (v6), the workflows from Preview (v5).");
    }

    [Fact]
    public void Not_yet_from_says_only_what_is_certain()
    {
        var old = new LocalCatalogState { Channel = CatalogChannel.Preview, Workloads = Section(6, null, "a"), Workflows = Section(6, null, "a") };

        InstalledCatalogDescription.NotYetFrom(old, CatalogChannel.Stable).Should().Be("The installed catalog (v6) is not from Stable yet.");
    }

    [Fact]
    public void An_unrecorded_channel_is_never_a_mismatch()
    {
        var old = new LocalCatalogState { Channel = CatalogChannel.Preview, Workloads = Section(5, null, "a"), Workflows = Section(5, null, "a") };

        InstalledCatalogDescription.Lagging(old, CatalogChannel.Stable).Should().BeNull();
    }

    [Fact]
    public void Only_workflows_installed_still_names_them()
    {
        var state = new LocalCatalogState { Workflows = Section(4, CatalogChannel.Stable, "abc1234") };

        InstalledCatalogDescription.Describe(state).Should().Be("Catalog v4 (Stable, abc1234)");
    }

    [Fact]
    public void No_state_says_so()
    {
        InstalledCatalogDescription.Describe(new LocalCatalogState()).Should().Be("Catalog: none recorded (no catalog-state.json)");
    }

    [Fact]
    public void Reads_the_state_the_options_point_at()
    {
        var installed = Path.Combine(_dir, "catalog");
        new LocalCatalogState { Channel = CatalogChannel.Stable, Workloads = Section(5, CatalogChannel.Stable, "51e1684") }.Save(installed);

        InstalledCatalogDescription.Describe(new CatalogOptions { InstalledCatalogPath = installed })
            .Should().Be("Catalog v5 (Stable, 51e1684)");
    }

    // The SDK's locator uses an override folder only when it has a catalog.json, and then the
    // installed state describes nothing the install reads.
    [Fact]
    public void An_override_the_sdk_would_use_is_named_instead()
    {
        var installed = Path.Combine(_dir, "catalog");
        new LocalCatalogState { Workloads = Section(5, CatalogChannel.Stable, "51e1684") }.Save(installed);
        var overrideDir = Path.Combine(_dir, "override");
        Directory.CreateDirectory(overrideDir);
        File.WriteAllText(Path.Combine(overrideDir, CatalogSchema.RootFileName), "{}");

        InstalledCatalogDescription.Describe(new CatalogOptions { InstalledCatalogPath = installed, LocalOverridePath = overrideDir })
            .Should().Be($"Catalog: local override at {overrideDir}");
    }

    [Fact]
    public void An_override_without_a_catalog_is_ignored_as_the_sdk_ignores_it()
    {
        var installed = Path.Combine(_dir, "catalog");
        new LocalCatalogState { Workloads = Section(5, CatalogChannel.Stable, "51e1684") }.Save(installed);

        InstalledCatalogDescription.Describe(new CatalogOptions { InstalledCatalogPath = installed, LocalOverridePath = Path.Combine(_dir, "missing") })
            .Should().Be("Catalog v5 (Stable, 51e1684)");
    }
}
