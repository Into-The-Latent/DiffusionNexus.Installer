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

    // A state written by SDK 2.0.0 records no channel per section; its stamp is all there is.
    [Fact]
    public void A_section_without_a_channel_falls_back_to_the_stamp()
    {
        var state = new LocalCatalogState { Channel = CatalogChannel.Preview, Workloads = Section(5, null, null) };

        InstalledCatalogDescription.Describe(state).Should().Be("Catalog v5 (Preview)");
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
