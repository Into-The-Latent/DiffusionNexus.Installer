using DiffusionNexus.Installer.Electron.Services;
using DiffusionNexus.Installer.Tests.Support;
using FluentAssertions;
using Xunit;

namespace DiffusionNexus.Installer.Tests.Services;

public class CatalogUpdateStartupCheckTests
{
    [Fact]
    public async Task Start_returns_at_once_and_runs_one_startup_check_in_the_background()
    {
        var coordinator = new StubCatalogUpdateCoordinator();
        var service = new CatalogUpdateStartupCheck(coordinator);

        var start = service.StartAsync(CancellationToken.None);

        start.IsCompleted.Should().BeTrue("a slow GitHub must never delay the window");
        await start;
        SpinWait.SpinUntil(() => coordinator.StartupChecks == 1, 10_000).Should().BeTrue("the startup check, which also applies when the setting is on");
        await service.StopAsync(CancellationToken.None);
    }

    // #53 round 2: with one folder per channel, a first catalog read that races the channel resolution
    // reads (and seeds) the Stable folder for a Preview user. The resolution is a local file read; the
    // network check is what must not delay the window.
    [Fact]
    public async Task Start_resolves_the_channel_before_it_returns()
    {
        var coordinator = new StubCatalogUpdateCoordinator();
        var service = new CatalogUpdateStartupCheck(coordinator);

        await service.StartAsync(CancellationToken.None);

        coordinator.ChannelResolutions.Should().Be(1);
        SpinWait.SpinUntil(() => coordinator.StartupChecks == 1, 10_000).Should().BeTrue();
    }
}
