using DiffusionNexus.Installer.Core.Wizard;

namespace DiffusionNexus.Installer.Electron.Services;

/// <summary>
/// Where "Back" on a side trip (/licenses, /updates, /debug) leads: the last screen of the install
/// flow the user was on, rather than always the welcome screen.
///
/// The top bar is on every flow screen, so a user can open Licences from the middle of a running
/// install -- and a Back that dropped them on the welcome screen read as the install having gone
/// away. The install lives in the singleton session, so returning to its URL rejoins it: while it
/// runs because the session says so, and once it has finished because of
/// <see cref="InstallOnScreen"/> below.
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

    /// <summary>
    /// The run whose Install stage the user is looking at, or null on every other screen. Set by
    /// the install page. It is what tells "came Back to the report I was reading" apart from
    /// "picked the same workload again": a finished run is only re-opened when it is still this
    /// one AND the user is returning to the very URL they left (<see cref="IsAt"/>). Without it a
    /// failed install's report vanished behind a fresh wizard after one look at Licences.
    /// </summary>
    public WizardPlan? InstallOnScreen { get; set; }

    /// <param name="baseRelativePath">As <c>NavigationManager.ToBaseRelativePath</c> returns it: no leading slash.</param>
    public void Remember(string baseRelativePath) => _path = Normalize(baseRelativePath);

    /// <summary>
    /// True when <paramref name="baseRelativePath"/> is the screen Back would lead to -- i.e. the
    /// page asking is being returned to (a side trip, a reconnect), not arrived at from elsewhere
    /// in the flow. Only meaningful before the page's own shell has rendered and recorded it.
    /// </summary>
    public bool IsAt(string baseRelativePath) =>
        string.Equals(_path, Normalize(baseRelativePath), StringComparison.OrdinalIgnoreCase);

    // Path only. A query or fragment is no part of any flow route today, and carrying one
    // along would be replaying input nobody chose to keep.
    private static string Normalize(string baseRelativePath)
    {
        var end = baseRelativePath.IndexOfAny(['?', '#']);
        return "/" + (end < 0 ? baseRelativePath : baseRelativePath[..end]).TrimStart('/');
    }
}
