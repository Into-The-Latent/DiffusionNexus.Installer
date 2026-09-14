namespace DiffusionNexus.Installer.Core.Host;

/// <summary>
/// The three things the buttons at the end of an install do outside the installer's own UI: show
/// the folder, start what was installed, and close the installer.
///
/// Behind an interface because all three leave the process — two start one, the third ends this
/// one — so the Install screen could otherwise only be tested by actually doing those things.
/// </summary>
public interface IPostInstallActions
{
    /// <summary>
    /// Whether this host can close the installer. False outside the Electron shell: a browser tab
    /// showing the app cannot close the process serving it, and a button that silently does
    /// nothing is worse than no button at all.
    /// </summary>
    bool CanCloseInstaller { get; }

    /// <summary>Opens <paramref name="folderPath"/> in the system file manager. Throws if it cannot.</summary>
    void OpenFolder(string folderPath);

    /// <summary>Starts the installed app via its launcher script. Throws if it cannot.</summary>
    void LaunchApp(string launcherScriptPath);

    /// <summary>Closes the installer. A no-op where <see cref="CanCloseInstaller"/> is false.</summary>
    Task CloseInstallerAsync();
}
