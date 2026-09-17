namespace DiffusionNexus.Installer.Electron.Services;

/// <summary>
/// The two calls <see cref="AppUpdateChecker"/> makes on Electron's auto-updater. A seam only
/// because <c>Electron.AutoUpdater</c> is a static that talks to a socket no test has.
/// </summary>
public interface IAppUpdaterShell
{
    /// <summary>False outside the Electron shell (plain <c>dotnet run</c>, tests): there is no updater to talk to.</summary>
    bool IsAvailable { get; }

    void SetAllowPrerelease(bool allow);

    Task CheckForUpdatesAsync();
}

/// <inheritdoc />
public sealed class ElectronAppUpdaterShell : IAppUpdaterShell
{
    public bool IsAvailable => ElectronHost.IsActive;

    public void SetAllowPrerelease(bool allow) => ElectronNET.API.Electron.AutoUpdater.AllowPrerelease = allow;

    public Task CheckForUpdatesAsync() => ElectronNET.API.Electron.AutoUpdater.CheckForUpdatesAsync();
}
