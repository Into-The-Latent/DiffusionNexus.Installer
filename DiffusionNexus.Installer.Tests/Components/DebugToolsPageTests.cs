#if DEBUG
using Bunit;
using DiffusionNexus.Installer.Core.Catalog;
using DiffusionNexus.Installer.Core.DevTools;
using DiffusionNexus.Installer.Core.Host;
using DiffusionNexus.Installer.Core.Updates;
using DiffusionNexus.Installer.Electron.Components.Pages;
using DiffusionNexus.Installer.SDK.Catalog;
using DiffusionNexus.Installer.SDK.Catalog.Packaging;
using DiffusionNexus.Installer.SDK.Models.Configuration;
using DiffusionNexus.Installer.Tests.Support;
using FluentAssertions;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using Xunit;

namespace DiffusionNexus.Installer.Tests.Components;

/// <summary>
/// The page is compiled out of Release builds (csproj), so this file goes with it. The channel
/// panel is the only way to switch channel without an environment variable, and a Debug run
/// that forgot the saved preference on every start would make Preview useless for the author.
/// </summary>
public class DebugToolsPageTests : BunitContext
{
    private readonly StubCatalogUpdateCoordinator _catalog = new();

    public DebugToolsPageTests()
    {
        var source = new Mock<IWorkloadSource>();
        source.Setup(s => s.GetInstallerWorkloadsAsync(It.IsAny<CancellationToken>()))
              .ReturnsAsync(Array.Empty<InstallationConfiguration>());
        source.SetupGet(s => s.Diagnostics).Returns(Array.Empty<CatalogDiagnostic>());
        Services.AddSingleton(source.Object);
        Services.AddSingleton(Mock.Of<IFolderPicker>());
        Services.AddSingleton(new LauncherScriptPreview());
        Services.AddSingleton<ICatalogUpdateCoordinator>(_catalog);
    }

    private static string Radio(CatalogChannel channel) => $"input[name='catalog-channel'][value='{channel}']";

    [Fact]
    public void Shows_the_channel_the_coordinator_follows()
    {
        _catalog.Channel = CatalogChannel.Preview;

        var cut = Render<DebugTools>();

        cut.Find(Radio(CatalogChannel.Preview)).HasAttribute("checked").Should().BeTrue();
        cut.Find(Radio(CatalogChannel.Stable)).HasAttribute("checked").Should().BeFalse();
    }

    [Fact]
    public void Picking_a_channel_saves_it_through_the_coordinator()
    {
        var cut = Render<DebugTools>();

        cut.Find(Radio(CatalogChannel.Preview)).Change("Preview");

        _catalog.ChannelSet.Should().Be(CatalogChannel.Preview);
        cut.WaitForAssertion(() => cut.Find(Radio(CatalogChannel.Preview)).HasAttribute("checked").Should().BeTrue());
    }

    [Fact]
    public void Disables_the_radios_when_the_environment_pins_the_channel()
    {
        _catalog.Channel = CatalogChannel.Preview;
        _catalog.ChannelSource = CatalogChannelSource.Environment;

        var cut = Render<DebugTools>();

        cut.Find(Radio(CatalogChannel.Preview)).HasAttribute("disabled").Should().BeTrue();
        cut.Find(Radio(CatalogChannel.Stable)).HasAttribute("disabled").Should().BeTrue();
        cut.Markup.Should().Contain("Set by DIFFUSIONNEXUS_CATALOG_CHANNEL for this run");
    }

    [Fact]
    public void Check_now_starts_a_check_and_goes_to_the_updates_page()
    {
        var cut = Render<DebugTools>();

        cut.FindAll("button").Single(b => b.TextContent.Trim() == "Check now").Click();

        _catalog.Checks.Should().Be(1);
        Services.GetRequiredService<NavigationManager>().Uri.Should().EndWith("/updates");
    }

    [Fact]
    public async Task Subscribes_to_the_coordinator_and_unsubscribes_on_dispose()
    {
        // A channel switch the coordinator refuses (a check or apply already in flight) is a
        // silent no-op on its side -- without this subscription the radios would keep showing the
        // switch the user just clicked instead of snapping back to what is actually in effect.
        var cut = Render<DebugTools>();
        _catalog.Subscribers.Should().Be(1);

        await DisposeComponentsAsync();

        _catalog.Subscribers.Should().Be(0);
    }
}
#endif
