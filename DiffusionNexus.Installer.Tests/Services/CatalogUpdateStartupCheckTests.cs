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
}
