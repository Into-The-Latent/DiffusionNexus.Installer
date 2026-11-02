using System.Text.RegularExpressions;

namespace DiffusionNexus.Installer.Electron.Services;

/// <summary>Where the app self-update stands, in the order a check normally moves through it.</summary>
public enum AppUpdateStatus
{
    NotChecked,

    /// <summary>Not running inside Electron (plain <c>dotnet run</c>, tests): there is no updater.</summary>
    Unavailable,

    /// <summary>Inside Electron but unpackaged: electron-updater skips the check without a single event.</summary>
    NotInstalledBuild,

    Checking,
    UpToDate,

    /// <summary>Found; electron-updater downloads it straight away.</summary>
    Downloading,

    /// <summary>Downloaded and waiting for "Restart and install".</summary>
    Ready,

    Failed,
}

/// <summary>
/// Process-wide sink for auto-updater activity: the log lines, and the state the top bar and
/// /updates read.
/// </summary>
/// <remarks>
/// Electron's <c>AutoUpdater</c> is a singleton owned by the main process, and its events fire on
/// Electron's socket thread rather than on a Blazor circuit. Subscribing to it directly from a
/// component leaks: the component cannot detach its handlers again (each lambda is a distinct
/// delegate instance, so <c>-=</c> silently removes nothing), and after a Blazor reconnect the old
/// handlers keep firing against a dead circuit.
///
/// So the Electron events are wired exactly once at startup into this service, and components
/// subscribe to <see cref="Changed"/> — a plain <see cref="Action"/> they CAN unsubscribe.
/// </remarks>
public sealed class UpdaterLog
{
    private readonly List<string> _lines = new();
    private readonly Lock _gate = new();

    /// <summary>Raised after every mutation. Handlers must marshal to their own sync context.</summary>
    public event Action? Changed;

    /// <summary>
    /// The updater answered a check: up to date, found, or failed. Not read from <see cref="Status"/>, which
    /// holds on to a download whatever a later check says. Raised before <see cref="Changed"/>: a check's
    /// turn hangs on it, and a throwing subscriber to that must not lose it.
    /// </summary>
    public event Action? CheckAnswered;

    public AppUpdateStatus Status { get; private set; }

    /// <summary>The version found or downloaded. Null until an update is found.</summary>
    public string? Version { get; private set; }

    /// <summary>Whole percent of the running download. Null until progress is reported.</summary>
    public int? DownloadPercent { get; private set; }

    /// <summary>The updater's message for <see cref="AppUpdateStatus.Failed"/>, cut down for the page. <see cref="Lines"/> has all of it.</summary>
    public string? Error { get; private set; }

    /// <summary>True from the moment an update is found, through its download, until it is installed.</summary>
    public bool UpdateAvailable => Status is AppUpdateStatus.Downloading or AppUpdateStatus.Ready;

    /// <summary>True once an update has been downloaded and is waiting to be installed.</summary>
    public bool UpdateReady => Status == AppUpdateStatus.Ready;

    public IReadOnlyList<string> Lines
    {
        get
        {
            lock (_gate)
            {
                return _lines.ToArray();
            }
        }
    }

    public void Append(string message)
    {
        lock (_gate)
        {
            _lines.Add(message);
        }

        // Also to stdout: updater problems are otherwise invisible unless someone is watching
        // the window, and the window is exactly what is unavailable when an update goes wrong.
        Console.WriteLine($"[updater] {message}");

        Changed?.Invoke();
    }

    public void MarkUnavailable() => Set(AppUpdateStatus.Unavailable);

    public void MarkNotInstalledBuild()
    {
        // Every check re-establishes this; the line says it once.
        var first = Status != AppUpdateStatus.NotInstalledBuild;
        Set(AppUpdateStatus.NotInstalledBuild);
        if (first) Append("This build is not installed, so there is no app update to check for.");
    }

    public void MarkChecking()
    {
        Set(AppUpdateStatus.Checking);
        Append("Checking for updates...");
    }

    public void MarkUpToDate()
    {
        CheckAnswered?.Invoke();
        Set(AppUpdateStatus.UpToDate);
        Append("No update available - this is the latest version.");
    }

    public void MarkAvailable(string version)
    {
        Version = version;
        DownloadPercent = null;

        // Set directly, not through Set: a newer release found after one already downloaded
        // replaces it, so Ready does not hold here.
        Status = AppUpdateStatus.Downloading;
        CheckAnswered?.Invoke();
        Append($"Update available: {version}. Downloading...");
    }

    public void MarkProgress(double percent)
    {
        var whole = (int)Math.Round(percent, MidpointRounding.AwayFromZero);
        if (DownloadPercent == whole) return;

        DownloadPercent = whole;

        // Progress is proof the download is alive. A failure may have ended Downloading (see
        // MarkFailed) when it was an overlapping check that failed, not the download.
        if (Status != AppUpdateStatus.Ready && Version is not null) Status = AppUpdateStatus.Downloading;
        Append($"Downloading... {whole}%");
    }

    public void MarkUpdateReady(string? version = null)
    {
        Version = version ?? Version;
        Status = AppUpdateStatus.Ready;
        Append($"Update {Version} downloaded and ready to install.");
    }

    public void MarkFailed(string error)
    {
        Error = Shortened(error);

        // An error is the only way a download ends other than Ready, and the event does not say
        // whether the download or an overlapping check failed -- so it must get through, or a
        // dead download would read "Downloading" for ever. MarkProgress undoes a false alarm.
        // A download already on disk is beyond any later error.
        if (Status != AppUpdateStatus.Ready) Status = AppUpdateStatus.Failed;
        CheckAnswered?.Invoke();
        Changed?.Invoke();
        Append($"Updater error: {error}");
    }

    /// <summary>
    /// A check nothing answered. Unlike an error event this knows it is about the check, so a download
    /// on disk or still running stays on offer (see <see cref="Set"/>).
    /// </summary>
    public void MarkNoAnswer(string message)
    {
        Error = message;
        Set(AppUpdateStatus.Failed);
        Append($"Updater error: {message}");
    }

    // electron-updater's HTTP failures: the status, then a quoted description naming the address, then
    // every response header. A 404's description also blames an authentication token nobody here has.
    private static readonly Regex HttpFailure = new(@"^(?<status>.*?)\s*""method: \w+ url: (?<url>[^\s\\""]+)", RegexOptions.Singleline);

    private const int ShownErrorLength = 200;

    /// <summary>What the page shows of a failure: the status and address of an HTTP failure, else the first line.</summary>
    private static string Shortened(string error)
    {
        var text = error.Trim();
        if (text.Length == 0) return "No details were given.";

        var http = HttpFailure.Match(text);
        var shown = http.Success
            ? $"{http.Groups["status"].Value.ReplaceLineEndings(" ")} from {http.Groups["url"].Value}"
            : text.ReplaceLineEndings("\n").Split('\n')[0];

        return shown.Length <= ShownErrorLength ? shown : shown[..ShownErrorLength] + "…";
    }

    private void Set(AppUpdateStatus status)
    {
        // A download on disk, or still running, stays on offer whatever a later check says:
        // re-checking (a channel switch does it by itself) fires "checking" and "not available"
        // while the update found earlier is still there, and neither the restart button nor
        // "Update Available" may disappear under the user.
        if (Status is not (AppUpdateStatus.Ready or AppUpdateStatus.Downloading)) Status = status;
        Changed?.Invoke();
    }
}
