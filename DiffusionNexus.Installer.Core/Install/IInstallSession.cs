using DiffusionNexus.Installer.Core.Wizard;
using DiffusionNexus.Installer.SDK.Models.Installation;
using DiffusionNexus.Installer.SDK.Services;

namespace DiffusionNexus.Installer.Core.Install;

/// <summary>
/// Owns a running installation for the lifetime of the app, not of a UI component.
/// Registered as a singleton: a Blazor circuit reconnect disposes components, and an install that
/// can run for hours must not go with them.
/// </summary>
public interface IInstallSession
{
    InstallPhase Phase { get; }
    InstallationProgress? Progress { get; }
    DownloadProgress? CurrentDownload { get; }
    IReadOnlyList<InstallLogLine> LogLines { get; }

    /// <summary>
    /// The last <paramref name="count"/> log lines, newest last. The live view renders this rather
    /// than <see cref="LogLines"/>: the buffer holds 5000 lines and changes are coalesced ~10x a
    /// second, so copying and re-joining the whole thing per render ships hundreds of kilobytes
    /// over the SignalR circuit to display one new line.
    /// </summary>
    IReadOnlyList<InstallLogLine> Tail(int count);

    /// <summary>
    /// A token cancelled when the run ends or is cancelled. Handed to prompts raised on the
    /// pipeline's behalf so a Cancel can complete a dialog the install is blocked on -- otherwise
    /// the pipeline thread waits forever on an answer and the only exit is killing the process.
    /// </summary>
    CancellationToken RunToken { get; }
    InstallationResult? Result { get; }

    /// <summary>
    /// The report rows recorded so far, so the result table can fill as the install runs instead
    /// of appearing all at once at the end. Empty until a run starts; replaced by
    /// <see cref="InstallationResult.Report"/> once the run finishes and that report has rows --
    /// the finished report is the authority, and it carries the "not run" rows an aborted run adds
    /// for steps that were never reached.
    /// </summary>
    IReadOnlyList<InstallReportEntry> ReportRows { get; }

    /// <summary>
    /// The plan of the current or most recent run, or null if nothing has started. Lets the UI tell
    /// a reconnect apart from a fresh navigation: the same plan instance means this is the run
    /// already under way, not a new one the user asked for.
    /// </summary>
    WizardPlan? Plan { get; }

    /// <summary>
    /// Where the finished run's log was written -- <c>installation-log-verbose-&lt;timestamp&gt;.txt</c>
    /// in the install folder, as the 1.x wizard wrote it -- or null while a run is going, when the
    /// install folder did not exist at the end, or when the file could not be written (issue #14).
    /// </summary>
    string? LogFilePath { get; }

    /// <summary>
    /// The whole buffer and how many older lines it has dropped this run, taken together under one
    /// lock -- for "Copy log", which is usable mid-install while lines are still being dropped. The
    /// count is zero for every run short enough to fit; when it is not, the copied text says so,
    /// because a log that starts mid-pip looks complete and is not.
    /// </summary>
    InstallLogSnapshot SnapshotLog();

    /// <summary>Raised when any of the above changes. Subscribers re-render; they never own state.</summary>
    event Action? Changed;

    Task StartAsync(WizardPlan plan, CancellationToken ct = default);
    void Cancel();
    void SkipCurrentDownload();
}
