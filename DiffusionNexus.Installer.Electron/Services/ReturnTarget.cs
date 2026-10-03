using DiffusionNexus.Installer.Core.Updates;
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
///
/// Unless the catalog changed since (#32): a screen is remembered with the catalog's
/// <see cref="ICatalogUpdateCoordinator.ContentGeneration"/>, and once an apply has landed content
/// Back leads to the welcome screen instead. With the inline link gone, Back is the one click to
/// the gallery that shows the change; the screen left behind was built from the old catalog, and
/// a workload screen or wizard returned to would show different content (or none, for a removed
/// workload) than the user left. Decided here, next to the state it guards, so it holds across a
/// circuit reconnect and whichever page the apply landed on. The stamp is the generation the
/// screen's content was read under, given by the page (see <see cref="Remember"/>).
/// </summary>
public sealed class ReturnTarget
{
    private sealed record Remembered(string Path, long Generation);

    private readonly ICatalogUpdateCoordinator _catalog;
    private readonly Lock _listeners = new();
    private volatile Remembered _at;
    private Action? _changed;
    private long _raisedGeneration;

    // One constructor on purpose: with a second, catalog-less one the container would quietly pick
    // it in a host that forgot the coordinator, and Back would never move after an apply.
    public ReturnTarget(ICatalogUpdateCoordinator catalog)
    {
        _catalog = catalog;
        _at = new("/", catalog.ContentGeneration);
    }

    /// <summary>
    /// Raised when an apply has moved <see cref="Path"/>; on the coordinator's thread. Listens to
    /// the coordinator only while someone listens here (a Back link on screen), so the pages'
    /// "every handler is gone after dispose" checks stay exact.
    /// </summary>
    public event Action? Changed
    {
        add
        {
            lock (_listeners)
            {
                if (_changed is null)
                {
                    Interlocked.Exchange(ref _raisedGeneration, _catalog.ContentGeneration);
                    _catalog.Changed += OnCatalogChanged;
                }
                _changed += value;
            }
        }
        remove
        {
            lock (_listeners)
            {
                _changed -= value;
                if (_changed is null) _catalog.Changed -= OnCatalogChanged;
            }
        }
    }

    /// <summary>App-relative, always rooted ("/", "/install/{id}").</summary>
    public string Path
    {
        get
        {
            var at = _at;
            return at.Generation == Generation || IsInstallOnScreen(at.Path) ? at.Path : "/";
        }
    }

    /// <summary>
    /// The run whose Install stage the user is looking at, or null on every other screen. Set by
    /// the install page. It is what tells "came Back to the report I was reading" apart from
    /// "picked the same workload again": a finished run is only re-opened when it is still this
    /// one AND the user is returning to the very URL they left (<see cref="IsAt"/>). Without it a
    /// failed install's report vanished behind a fresh wizard after one look at Licences.
    /// </summary>
    public WizardPlan? InstallOnScreen { get; set; }

    /// <param name="baseRelativePath">As <c>NavigationManager.ToBaseRelativePath</c> returns it: no leading slash.</param>
    /// <param name="readUnder">
    /// The <see cref="ICatalogUpdateCoordinator.ContentGeneration"/> the screen's content was read
    /// under, taken by the page before its catalog read. Null for a screen that shows no catalog
    /// content (it is stamped with the current generation). A page that reads the catalog must
    /// pass it: taken here, after the read, an apply landing in between would stamp stale content
    /// as current.
    /// </param>
    public void Remember(string baseRelativePath, long? readUnder = null) =>
        _at = new(Normalize(baseRelativePath), readUnder ?? Generation);

    /// <summary>
    /// True when <paramref name="baseRelativePath"/> is the screen Back would lead to -- i.e. the
    /// page asking is being returned to (a side trip, a reconnect), not arrived at from elsewhere
    /// in the flow. Only meaningful before the page's own shell has rendered and recorded it.
    /// </summary>
    public bool IsAt(string baseRelativePath) =>
        string.Equals(Path, Normalize(baseRelativePath), StringComparison.OrdinalIgnoreCase);

    private long Generation => _catalog.ContentGeneration;

    // A run's progress or report does not depend on the catalog, so an apply leaves the way back
    // to it alone. Only when it is the screen remembered: InstallOnScreen is not cleared when the
    // user leaves a report by the top bar, and must not shield the screen they went on to.
    private bool IsInstallOnScreen(string path) =>
        InstallOnScreen is { } plan
        && string.Equals(path, Normalize($"install/{plan.Selection.Workload.Id}"), StringComparison.OrdinalIgnoreCase);

    private void OnCatalogChanged()
    {
        var generation = Generation;
        // The only subscriber, BackLink, hands the work to its dispatcher and cannot throw here.
        if (Interlocked.Exchange(ref _raisedGeneration, generation) != generation) _changed?.Invoke();
    }

    // Path only. A query or fragment is no part of any flow route today, and carrying one
    // along would be replaying input nobody chose to keep.
    private static string Normalize(string baseRelativePath)
    {
        var end = baseRelativePath.IndexOfAny(['?', '#']);
        return "/" + (end < 0 ? baseRelativePath : baseRelativePath[..end]).TrimStart('/');
    }
}
