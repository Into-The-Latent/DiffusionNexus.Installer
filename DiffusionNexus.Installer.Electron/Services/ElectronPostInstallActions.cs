using DiffusionNexus.Installer.Core.Host;
using DiffusionNexus.Installer.SDK.Services;

// Same collision ElectronFolderPicker documents: this project's root namespace ends in `Electron`,
// so an unqualified `Electron` here binds to the namespace, not to ElectronNET's static class.
using ElectronApi = ElectronNET.API.Electron;

namespace DiffusionNexus.Installer.Electron.Services;

/// <summary>
/// The end-of-install buttons, for real. Showing the folder and starting the app are plain
/// processes on the host — the SDK helper already knows how to start a .bat, a .sh or an exe, and
/// is the same code the 1.x wizard's buttons used — while closing the installer is Electron's job.
/// </summary>
public sealed class ElectronPostInstallActions : IPostInstallActions
{
    public bool CanCloseInstaller => ElectronHost.IsActive;

    public void OpenFolder(string folderPath) => PostInstallLaunchHelper.OpenFolder(folderPath);

    public void LaunchApp(string launcherScriptPath) => PostInstallLaunchHelper.LaunchApp(launcherScriptPath);

    public Task CloseInstallerAsync()
    {
        // Quit(), not the window's Close(): closing the last window leaves the ASP.NET host and the
        // Electron main process alive on Windows, and the user's install session ends with an
        // invisible process still holding the port.
        if (ElectronHost.IsActive) ElectronApi.App.Quit();

        return Task.CompletedTask;
    }
}
