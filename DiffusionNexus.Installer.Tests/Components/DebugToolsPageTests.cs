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
    public void A_refused_switch_says_so_and_still_renders_the_channel_in_effect()
    {
        // The coordinator refuses silently (no Changed). What this cannot show: bUnit rebuilds its
        // DOM on every render, so the browser keeping the clicked dot (Blazor patches nothing when
        // "checked" renders the same values) is invisible here -- the @key on the radios handles
        // that, and docs/manual-smoke.md section 7 is where it is proven.
        _catalog.RefuseChannelChange = true;
        var cut = Render<DebugTools>();

        cut.Find(Radio(CatalogChannel.Preview)).Change("Preview");

        cut.Markup.Should().Contain("The channel was not switched");
        cut.Find(Radio(CatalogChannel.Stable)).HasAttribute("checked").Should().BeTrue();
        cut.Find(Radio(CatalogChannel.Preview)).HasAttribute("checked").Should().BeFalse();
    }

    [Fact]
    public void A_failed_save_shows_the_error_and_the_channel_still_in_effect()
    {
        _catalog.ChannelSaveFailure = new IOException("settings.json is locked");
        var cut = Render<DebugTools>();

        cut.Find(Radio(CatalogChannel.Preview)).Change("Preview");

        cut.Markup.Should().Contain("The channel could not be saved: settings.json is locked");
        cut.Find(Radio(CatalogChannel.Stable)).HasAttribute("checked").Should().BeTrue();
    }

    [Fact]
    public void A_switch_that_took_shows_no_error()
    {
        var cut = Render<DebugTools>();

        cut.Find(Radio(CatalogChannel.Preview)).Change("Preview");

        cut.FindAll(".validation-error").Should().BeEmpty();
    }

    [Theory]
    [InlineData(CatalogUpdatePhase.Checking)]
    [InlineData(CatalogUpdatePhase.Applying)]
    public void Disables_the_radios_while_the_coordinator_would_refuse_a_switch(CatalogUpdatePhase phase)
    {
        _catalog.Phase = phase;
        var cut = Render<DebugTools>();

        cut.Find(Radio(CatalogChannel.Preview)).HasAttribute("disabled").Should().BeTrue();

        _catalog.Phase = CatalogUpdatePhase.Checked;
        _catalog.RaiseChanged();

        cut.WaitForAssertion(() => cut.Find(Radio(CatalogChannel.Preview)).HasAttribute("disabled").Should().BeFalse());
    }

    [Fact]
    public async Task Subscribes_to_the_coordinator_and_unsubscribes_on_dispose()
    {
        var cut = Render<DebugTools>();
        _catalog.Subscribers.Should().Be(1);

        await DisposeComponentsAsync();

        _catalog.Subscribers.Should().Be(0);
    }
}
#endif
