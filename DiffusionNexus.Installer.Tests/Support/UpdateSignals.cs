using DiffusionNexus.Installer.Core.Updates;
using DiffusionNexus.Installer.Electron.Services;
using Microsoft.Extensions.DependencyInjection;

namespace DiffusionNexus.Installer.Tests.Support;

/// <summary>
/// What every screen that renders <c>TopBar</c> now needs: the two update signals the bar's dot
/// reads. One call per fixture, and the fixture keeps the handles to drive them.
/// </summary>
internal static class UpdateSignals
{
    public static (StubCatalogUpdateCoordinator Catalog, UpdaterLog App) Register(IServiceCollection services)
    {
        var catalog = new StubCatalogUpdateCoordinator();
        var app = new UpdaterLog();
        services.AddSingleton<ICatalogUpdateCoordinator>(catalog);
        services.AddSingleton(app);
        return (catalog, app);
    }
}
