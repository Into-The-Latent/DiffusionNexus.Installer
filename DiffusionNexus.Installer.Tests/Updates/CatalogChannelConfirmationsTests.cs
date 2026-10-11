using DiffusionNexus.Installer.Core.Updates;
using DiffusionNexus.Installer.SDK.Catalog.Packaging;
using DiffusionNexus.Installer.SDK.Catalog.Updates;
using FluentAssertions;
using Xunit;

namespace DiffusionNexus.Installer.Tests.Updates;

public sealed class CatalogChannelConfirmationsTests
{
    private static LocalCatalogState State(int version, CatalogChannel channel) => new()
    {
        Workloads = new SectionState(version, "abc", DateTimeOffset.UnixEpoch) { Channel = channel },
        Workflows = new SectionState(version, "abc", DateTimeOffset.UnixEpoch) { Channel = channel },
    };

    // One installed folder (SDK 2.0-2.2): an apply replaces the content, so another channel's
    // confirmation of content that differs describes nothing any more.
    [Fact]
    public void With_one_folder_confirming_other_content_drops_the_other_channel()
    {
        var confirmations = new CatalogChannelConfirmations(null);
        confirmations.Confirm(CatalogChannel.Preview, State(6, CatalogChannel.Preview));

        confirmations.Confirm(CatalogChannel.Stable, State(4, CatalogChannel.Stable));

        confirmations.Current.Keys.Should().Equal(CatalogChannel.Stable);
    }

    // One folder per channel (SDK 2.3.0, #53 review): the other channel's folder is untouched by this
    // apply, so its confirmation still describes it.
    [Fact]
    public void With_one_folder_per_channel_confirming_one_channel_keeps_the_others()
    {
        var confirmations = new CatalogChannelConfirmations(null, perChannelFolders: true);
        confirmations.Confirm(CatalogChannel.Preview, State(6, CatalogChannel.Preview));

        confirmations.Confirm(CatalogChannel.Stable, State(4, CatalogChannel.Stable));

        confirmations.Current.Keys.Should().BeEquivalentTo([CatalogChannel.Preview, CatalogChannel.Stable]);
    }
}
