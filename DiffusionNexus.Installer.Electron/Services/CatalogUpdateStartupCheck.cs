using DiffusionNexus.Installer.Core.Updates;
using Microsoft.Extensions.Logging.Abstractions;

namespace DiffusionNexus.Installer.Electron.Services;

/// <summary>
/// One catalog check when the app starts, next to the app self-update check in Program.cs. An
/// installer is a short-lived, occasionally-run app: if it waited for the user to ask, most
/// installs would never see a newer catalog. Fire-and-forget so a slow or unreachable GitHub
/// cannot delay the window. Unlike the app updater it does not need Electron, so it also runs
/// under plain `dotnet run`.
/// </summary>
public sealed class CatalogUpdateStartupCheck(ICatalogUpdateCoordinator coordinator, ILogger<CatalogUpdateStartupCheck>? logger = null) : IHostedService
{
    private readonly ILogger<CatalogUpdateStartupCheck> _logger = logger ?? NullLogger<CatalogUpdateStartupCheck>.Instance;

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _ = Task.Run(async () =>
        {
            try
            {
                await coordinator.CheckAsync(CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                // CheckAsync never throws; this keeps an unobserved task exception from ever
                // becoming the reason the app looks broken.
                _logger.LogWarning(ex, "Startup catalog update check failed");
            }
        }, CancellationToken.None);
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
