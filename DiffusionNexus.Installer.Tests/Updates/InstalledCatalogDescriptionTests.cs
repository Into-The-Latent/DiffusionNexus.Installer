using DiffusionNexus.Installer.Core.Updates;
using DiffusionNexus.Installer.SDK.Catalog;
using DiffusionNexus.Installer.SDK.Catalog.Packaging;
using DiffusionNexus.Installer.SDK.Catalog.Updates;
using FluentAssertions;
using Moq;
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

            var reading = new CatalogProvenance(options, Mock.Of<ICatalog>()).Read();
            reading.Failed.Should().BeTrue();
            reading.Text.Should().StartWith("Catalog: could not be read: ");
        }

        new CatalogProvenance(options, Mock.Of<ICatalog>()).Read().Should().Be(new InstalledCatalogReading("Catalog v5 (Stable, 51e1684)", false));
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

    // PR #43 review round 2: judged per section. A section is from the channel it records or the
    // channel a check found it current on; one that is neither is lagging only after this run's
    // switch to the followed channel did not land.
    [Fact]
    public void A_section_a_check_found_current_on_the_followed_channel_is_not_lagging()
    {
        var shared = new LocalCatalogState { Workloads = Section(5, CatalogChannel.Preview, "a"), Workflows = Section(5, CatalogChannel.Preview, "a") };
        var evidence = new CatalogSourceEvidence
        {
            Confirmed = new Dictionary<CatalogChannel, ConfirmedSections> { [CatalogChannel.Stable] = new(shared.Workloads, shared.Workflows) },
        };

        InstalledCatalogDescription.Lagging(shared, CatalogChannel.Stable, evidence).Should().BeNull();

        var later = new LocalCatalogState { Workloads = Section(6, CatalogChannel.Preview, "b"), Workflows = shared.Workflows };
        InstalledCatalogDescription.Lagging(later, CatalogChannel.Stable, evidence).Should().Be("The installed workloads are still from Preview (v6).");
    }

    [Fact]
    public void An_unrecorded_section_a_check_found_current_on_the_other_channel_is_from_that_channel()
    {
        var old = new LocalCatalogState { Channel = CatalogChannel.Preview, Workloads = Section(5, null, "a"), Workflows = Section(5, null, "a") };
        var evidence = new CatalogSourceEvidence
        {
            Confirmed = new Dictionary<CatalogChannel, ConfirmedSections> { [CatalogChannel.Preview] = new(old.Workloads, old.Workflows) },
        };

        InstalledCatalogDescription.Lagging(old, CatalogChannel.Stable, evidence).Should().Be("The installed catalog is still from Preview (v5).");
        InstalledCatalogDescription.Lagging(old, CatalogChannel.Preview, evidence).Should().BeNull();
    }

    [Fact]
    public void An_unrecorded_section_is_lagging_only_after_a_switch_to_the_followed_channel_did_not_land()
    {
        var old = new LocalCatalogState { Channel = CatalogChannel.Preview, Workloads = Section(6, null, "a"), Workflows = Section(6, null, "a") };

        InstalledCatalogDescription.Lagging(old, CatalogChannel.Stable).Should().BeNull();
        InstalledCatalogDescription.Lagging(old, CatalogChannel.Stable, new CatalogSourceEvidence { NotLandedTo = CatalogChannel.Stable })
            .Should().Be("The installed catalog (v6) is not from Stable yet.");
        InstalledCatalogDescription.Lagging(old, CatalogChannel.Preview, new CatalogSourceEvidence { NotLandedTo = CatalogChannel.Stable })
            .Should().BeNull();
    }

    [Fact]
    public void A_partial_apply_over_an_unrecorded_state_names_the_section_left_behind()
    {
        var partial = new LocalCatalogState { Workloads = Section(4, CatalogChannel.Stable, "a"), Workflows = Section(5, null, "b") };

        InstalledCatalogDescription.Lagging(partial, CatalogChannel.Stable, new CatalogSourceEvidence { NotLandedTo = CatalogChannel.Stable })
            .Should().Be("The installed workflows (v5) are not from Stable yet.");
    }

    [Fact]
    public void A_known_and_an_unknown_lagging_section_are_both_named()
    {
        var state = new LocalCatalogState { Workloads = Section(6, CatalogChannel.Preview, "a"), Workflows = Section(5, null, "b") };

        InstalledCatalogDescription.Lagging(state, CatalogChannel.Stable, new CatalogSourceEvidence { NotLandedTo = CatalogChannel.Stable })
            .Should().Be("The installed workloads are still from Preview (v6). The installed workflows (v5) are not from Stable yet.");
    }

    // PR #43 review round 2: the wizard's reading is the state of the load its workloads came
    // from, not a second read of a file an apply may have rewritten in between.
    [Fact]
    public void The_state_loaded_with_the_workloads_is_described_not_the_file()
    {
        var installed = Path.Combine(_dir, "catalog");
        new LocalCatalogState { Workloads = Section(6, CatalogChannel.Preview, "2df647e") }.Save(installed);
        var loaded = new LocalCatalogState { Workloads = Section(5, CatalogChannel.Stable, "51e1684") };

        InstalledCatalogDescription.Describe(new CatalogOptions { InstalledCatalogPath = installed }, loaded)
            .Should().Be("Catalog v5 (Stable, 51e1684)");
    }

    // A load whose state file was held answers an empty state (ICatalog reads it tolerantly): then
    // the file is read, and a file still held is a failed reading, not "none recorded".
    [Fact]
    public void A_load_that_recorded_nothing_reads_the_file()
    {
        var installed = Path.Combine(_dir, "catalog");
        new LocalCatalogState { Workloads = Section(5, CatalogChannel.Stable, "51e1684") }.Save(installed);
        var options = new CatalogOptions { InstalledCatalogPath = installed };

        InstalledCatalogDescription.Describe(options, new LocalCatalogState()).Should().Be("Catalog v5 (Stable, 51e1684)");
        using (new FileStream(Path.Combine(installed, CatalogSchema.StateFileName), FileMode.Open, FileAccess.Read, FileShare.None))
        {
            var describe = () => InstalledCatalogDescription.Describe(options, new LocalCatalogState());
            describe.Should().Throw<IOException>();
        }
    }

    [Fact]
    public async Task The_reading_comes_from_the_same_load_as_what_was_read_with_it()
    {
        var before = new LocalCatalogState { Workloads = Section(6, CatalogChannel.Preview, "2df647e") };
        var after = new LocalCatalogState { Workloads = Section(5, CatalogChannel.Stable, "51e1684") };
        var current = before;
        var catalog = new Mock<ICatalog>();
        catalog.SetupGet(c => c.State).Returns(() => current);
        var provenance = new CatalogProvenance(new CatalogOptions { InstalledCatalogPath = Path.Combine(_dir, "none") }, catalog.Object);
        var reads = 0;

        // The first read straddles an apply: the catalog is invalidated while it runs.
        var (value, reading) = await provenance.ReadWithAsync(_ =>
        {
            if (++reads == 1) current = after;
            return Task.FromResult(ReferenceEquals(current, before) ? "Preview v6 workloads" : "Stable v5 workloads");
        });

        reads.Should().Be(2, "a read that straddled a reload is taken again");
        value.Should().Be("Stable v5 workloads");
        reading.Should().Be(new InstalledCatalogReading("Catalog v5 (Stable, 51e1684)", false));
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
