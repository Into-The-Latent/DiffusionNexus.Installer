using DiffusionNexus.Installer.Core.Updates;
using DiffusionNexus.Installer.Electron.Services;
using Microsoft.Extensions.DependencyInjection;

namespace DiffusionNexus.Installer.Tests.Support;

/// <summary>
/// What every screen that wears the shell now needs: the two update signals the bar's dot
/// reads, and the <see cref="ReturnTarget"/> the shell records itself in. One call per fixture,
/// and the fixture keeps the handles to drive the signals.
/// </summary>
internal static class UpdateSignals
{
    public static (StubCatalogUpdateCoordinator Catalog, UpdaterLog App) Register(IServiceCollection services)
    {
        var catalog = new StubCatalogUpdateCoordinator();
        var app = new UpdaterLog();
        services.AddSingleton<ICatalogUpdateCoordinator>(catalog);
        services.AddSingleton(app);
        services.AddSingleton<ReturnTarget>();
        return (catalog, app);
    }
}
