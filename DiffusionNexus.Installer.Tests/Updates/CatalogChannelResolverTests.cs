using DiffusionNexus.Installer.Core.Updates;
using DiffusionNexus.Installer.SDK.Catalog.Packaging;
using FluentAssertions;
using Xunit;

namespace DiffusionNexus.Installer.Tests.Updates;

/// <summary>
/// The one rule that decides which release the installer reads: the environment wins (a tester
/// flips it for one run without touching saved state), then the saved setting, then Stable.
/// </summary>
public class CatalogChannelResolverTests
{
    [Fact]
    public void Nothing_set_means_stable_by_default()
    {
        CatalogChannelResolver.Resolve(null, null)
            .Should().Be((CatalogChannel.Stable, CatalogChannelSource.Default));
    }

    [Theory]
    [InlineData("Preview", CatalogChannel.Preview)]
    [InlineData("preview", CatalogChannel.Preview)]
    [InlineData("  STABLE ", CatalogChannel.Stable)]
    public void The_saved_setting_is_read_case_insensitively(string saved, CatalogChannel expected)
    {
        CatalogChannelResolver.Resolve(null, saved)
            .Should().Be((expected, CatalogChannelSource.Setting));
    }

    [Fact]
    public void The_environment_beats_the_saved_setting()
    {
        CatalogChannelResolver.Resolve("stable", "Preview")
            .Should().Be((CatalogChannel.Stable, CatalogChannelSource.Environment));
    }

    [Fact]
    public void An_unrecognised_environment_value_falls_through_to_the_setting()
    {
        CatalogChannelResolver.Resolve("nightly", "Preview")
            .Should().Be((CatalogChannel.Preview, CatalogChannelSource.Setting));
    }

    [Fact]
    public void An_unrecognised_setting_falls_through_to_stable()
    {
        CatalogChannelResolver.Resolve(null, "1")
            .Should().Be((CatalogChannel.Stable, CatalogChannelSource.Default),
                "Enum.TryParse would accept the digit; a channel is a word");
    }

    [Fact]
    public void The_variable_name_is_the_documented_one()
    {
        CatalogChannelResolver.EnvironmentVariable.Should().Be("DIFFUSIONNEXUS_CATALOG_CHANNEL");
    }
}
