namespace DiffusionNexus.Installer.Electron.Services;

/// <summary>
/// Where "Back" on a side trip (/licenses, /updates, /debug) leads: the last screen of the install
/// flow the user was on, rather than always the welcome screen.
///
/// The top bar is on every flow screen, so a user can open Licences from the middle of a running
/// install -- and a Back that dropped them on the welcome screen read as the install having gone
/// away. The install itself lives in the singleton session, so returning to its URL rejoins it.
///
/// A singleton, like the session: this is a one-user desktop app, and a circuit reconnect must
/// not forget where the user was. Written by <c>ScreenShell</c> -- every flow screen wears it and
/// no side trip does, so "rendered the shell" is exactly "is somewhere worth returning to".
/// </summary>
public sealed class ReturnTarget
{
    private volatile string _path = "/";

    /// <summary>App-relative, always rooted ("/", "/install/{id}").</summary>
    public string Path => _path;

    /// <param name="baseRelativePath">As <c>NavigationManager.ToBaseRelativePath</c> returns it: no leading slash.</param>
    public void Remember(string baseRelativePath)
    {
        // Path only. A query or fragment is no part of any flow route today, and carrying one
        // along would be replaying input nobody chose to keep.
        var end = baseRelativePath.IndexOfAny(['?', '#']);
        _path = "/" + (end < 0 ? baseRelativePath : baseRelativePath[..end]).TrimStart('/');
    }
}
