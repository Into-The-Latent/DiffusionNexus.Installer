using DiffusionNexus.Installer.Core.Content;
using DiffusionNexus.Installer.Core.Updates;
using DiffusionNexus.Installer.Core.Wizard;
using DiffusionNexus.Installer.SDK.Models.Installation;
using DiffusionNexus.Installer.SDK.Services;
using SdkLogLevel = DiffusionNexus.Installer.SDK.Models.Enums.LogLevel;

namespace DiffusionNexus.Installer.Core.Install;

/// <inheritdoc cref="IInstallSession"/>
public sealed class InstallSession : IInstallSession, IDisposable
{
    /// <summary>pip output floods; the buffer is bounded so a long install cannot grow unbounded.</summary>
    public const int MaxLogLines = 5000;

    /// <summary>
    /// How often coalesced changes reach subscribers. Raising Changed per log line would push a
    /// render over the SignalR circuit for every line pip prints, which no browser survives.
    /// </summary>
    public static readonly TimeSpan DefaultFlushInterval = TimeSpan.FromMilliseconds(100);

    private readonly IInstallationOrchestrator _orchestrator;
    private readonly TimeSpan _flushInterval;
    private readonly ICatalogProvenance? _catalog;
    private readonly Lock _gate = new();
    private readonly Queue<InstallLogLine> _log = new();
    private readonly List<InstallReportEntry> _reportRows = [];
    private readonly Timer _flushTimer;
    private int _dirty;
    private int _truncatedLogLines;
    private CancellationTokenSource? _cts;
    private CancellationTokenSource? _skipDownloadCts;

    // The current run's catalog row (spec 7.2), kept so the finished report cannot drop it.
    private InstallReportEntry? _catalogRow;

    /// <param name="catalog">
    /// Reads the catalog line for a plan whose selection did not capture one. Null, and a plan
    /// without one records no catalog line.
    /// </param>
    public InstallSession(IInstallationOrchestrator orchestrator, TimeSpan? flushInterval = null, ICatalogProvenance? catalog = null)
    {
        _orchestrator = orchestrator;
        _flushInterval = flushInterval ?? DefaultFlushInterval;
        _catalog = catalog;
        _flushTimer = new Timer(_ => Flush(), null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
    }

    public InstallPhase Phase { get; private set; } = InstallPhase.Idle;
    public InstallationProgress? Progress { get; private set; }
    public DownloadProgress? CurrentDownload { get; private set; }
    public InstallationResult? Result { get; private set; }
    public WizardPlan? Plan { get; private set; }

    /// <inheritdoc/>
    public string? LogFilePath { get; private set; }

    /// <inheritdoc/>
    public InstallLogSnapshot SnapshotLog()
    {
        lock (_gate) return new InstallLogSnapshot([.. _log], _truncatedLogLines);
    }

    public IReadOnlyList<InstallLogLine> LogLines
    {
        get { lock (_gate) return [.. _log]; }
    }

    /// <inheritdoc/>
    public IReadOnlyList<InstallReportEntry> ReportRows
    {
        get { lock (_gate) return [.. _reportRows]; }
    }

    /// <inheritdoc/>
    public IReadOnlyList<InstallLogLine> Tail(int count)
    {
        if (count <= 0) return [];

        lock (_gate)
        {
            if (_log.Count <= count) return [.. _log];

            // Skip is O(n) over a Queue either way, but it allocates only the tail -- which is the
            // point: the whole-buffer copy happened under the same lock OnLog needs, so a fast
            // renderer back-pressured the installer's own log producer.
            var tail = new InstallLogLine[count];
            var i = 0;
            var skip = _log.Count - count;

            foreach (var line in _log)
            {
                if (skip-- > 0) continue;
                tail[i++] = line;
            }

            return tail;
        }
    }

    /// <inheritdoc/>
    public CancellationToken RunToken
    {
        get { lock (_gate) return _cts?.Token ?? CancellationToken.None; }
    }

    public event Action? Changed;

    public async Task StartAsync(WizardPlan plan, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(plan);

        lock (_gate)
        {
            if (Phase == InstallPhase.Running)
                throw new InvalidOperationException("An installation is already running.");

            Phase = InstallPhase.Running;
            Plan = plan;
            Result = null;
            Progress = null;
            CurrentDownload = null;
            LogFilePath = null;
            _truncatedLogLines = 0;
            _log.Clear();
            _reportRows.Clear();
            _catalogRow = null;
        }

        try
        {
            // Inside the try: a throw here used to escape uncaught and wedge Phase at Running.
            // Locked, like _skipDownloadCts below, so Cancel() reading _cts under the same gate can
            // never observe a torn or stale value -- see Cancel().
            lock (_gate) _cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            lock (_gate) _skipDownloadCts = new CancellationTokenSource();
            RecordCatalog(plan);
            NotifyNow();
            _flushTimer.Change(_flushInterval, _flushInterval);

            // ToOptions runs every module's Contribute, which is also what writes the chosen folder
            // into the selection -- so it must run before the folder is read, not as an argument
            // beside it. Argument evaluation order made this work only by accident.
            // The report callback rides on the options rather than beside the progress reporters:
            // that is where the SDK takes it, and it means every caller path -- not only the one
            // that reaches the orchestrator directly -- can watch the report fill.
            var options = plan.ToOptions() with { OnReportRow = OnReportRow };
            var targetDirectory = plan.Selection.TargetFolder;

            var result = await _orchestrator.InstallAsync(
                plan.Selection.Workload,
                targetDirectory,
                options,
                new InlineProgress<InstallLogEntry>(OnLog),
                new InlineProgress<InstallationProgress>(OnStep),
                new InlineProgress<DownloadProgress>(OnDownload),
                GetSkipDownloadToken,
                _cts.Token).ConfigureAwait(false);

            // The finished report wins over the rows streamed during the run. They hold the same
            // rows in the same order, but only the finished one carries what an aborted run adds
            // at the very end for steps it never reached. A result built without a report -- the
            // catch blocks below -- leaves the streamed rows alone rather than blanking a table
            // the user watched fill up.
            if (result.Report.Count > 0)
            {
                lock (_gate)
                {
                    _reportRows.Clear();
                    // The SDK's report knows nothing of the catalog row; it stays first.
                    if (_catalogRow is not null) _reportRows.Add(_catalogRow);
                    _reportRows.AddRange(result.Report);
                }
            }

            Result = result;
            Phase = result.IsCancelled ? InstallPhase.Cancelled
                  : result.IsSuccess ? InstallPhase.Completed
                  : InstallPhase.Failed;
        }
        catch (OperationCanceledException)
        {
            Result = InstallationResult.Cancelled("Installation cancelled.");
            Phase = InstallPhase.Cancelled;
        }
        catch (Exception ex)
        {
            // The UI must always end with a truthful outcome; an escaping exception would leave
            // the Install screen stuck on "Running" forever.
            Result = InstallationResult.Failure($"Installation failed: {ex.Message}");
            Phase = InstallPhase.Failed;
        }
        finally
        {
            // Stop coalescing before the final notification, so the terminal state is never left
            // sitting in the buffer waiting for a tick that no longer comes. Guarded because a
            // concurrent Dispose may already have torn the timer down.
            try { _flushTimer.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan); }
            catch (ObjectDisposedException) { }

            // After the outcome is known -- the file header names it -- and before the final
            // notification, so the "Log saved to" line is in the last render.
            WriteLogFile(plan);

            NotifyNow();
        }
    }

    /// <summary>
    /// Spec 7.2: one log line and one report row naming the catalog this run's workload came from
    /// -- captured by the wizard when it read the workload, else read now -- so a support question
    /// is answered from the report. A capture that could not read the state is read again: what
    /// held the file then (the switch's apply writing it, an AV scan) has usually let go by now
    /// (PR #43 review). A catalog that cannot be read is a warning row, never a reason not to install.
    /// </summary>
    private void RecordCatalog(WizardPlan plan)
    {
        var captured = plan.Selection.Catalog;
        var reading = captured is { Failed: false } ? captured : _catalog?.Read() ?? captured;
        if (reading is null) return;
        var (text, warning) = (reading.Text, reading.Failed);

        var row = new InstallReportEntry
        {
            PlannedOperation = text,
            Category = InstallReportCategory.Step,
            Outcome = warning ? InstallReportOutcome.Skipped : InstallReportOutcome.Success,
            Comment = "The catalog this install reads.",
            IsWarning = warning,
        };
        Append(new InstallLogLine(DateTimeOffset.Now, text, warning ? SdkLogLevel.Warning : SdkLogLevel.Info));
        lock (_gate)
        {
            _catalogRow = row;
            _reportRows.Add(row);
        }
    }

    /// <summary>
    /// What the 1.x wizard did when a run ended: the whole log into a timestamped file in the
    /// install folder (see <see cref="LogFolder"/>), so a user can find and send it without the
    /// installer still being open (issue #14). Nowhere to write it -- the folder never got created -- means no file, never a
    /// failed run; InstallLogFile.TryWrite owns that rule.
    /// </summary>
    private void WriteLogFile(WizardPlan plan)
    {
        var now = DateTimeOffset.Now;
        var (lines, truncated) = SnapshotLog();

        var outcome = Result is null ? Phase.ToString() : $"{Phase}: {Result.Message}";
        var text = InstallLogFile.Compose(
            plan.Selection.Workload.Name, plan.Selection.TargetFolder, outcome, now, lines, truncated);

        var path = InstallLogFile.TryWrite(LogFolder(plan.Selection), text, now);
        if (path is null) return;

        LogFilePath = path;
        Append(new InstallLogLine(now, $"Log saved to: {path}", SdkLogLevel.Success));
    }

    /// <summary>
    /// Where the log goes: the chosen folder, as the 1.x wizard did -- except while the chosen
    /// folder is itself the install ("E:\ComfyUI" installs into E:\ComfyUI) and the run put
    /// nothing in it yet, or never created it. A log there would make the install-folder check
    /// refuse the retry as "not empty", so it goes beside the install instead (E:\) -- the same
    /// place whether or not the user had made the empty folder first. If that place refuses the file
    /// (C:\ for a standard user) there is no file: the on-screen log and Copy log remain, and a
    /// blocked retry is the worse outcome. Never throws, like TryWrite: it runs in StartAsync's
    /// <c>finally</c>, ahead of the notification that ends the run on screen.
    /// </summary>
    private static string? LogFolder(WizardSelection selection)
    {
        var chosen = selection.TargetFolder;
        try
        {
            if (string.IsNullOrWhiteSpace(chosen)) return null;

            var install = RepositoryPaths.Resolve(selection.Workload, chosen);
            var chosenIsTheInstall = string.Equals(
                Path.TrimEndingDirectorySeparator(Path.GetFullPath(install)),
                Path.TrimEndingDirectorySeparator(Path.GetFullPath(chosen)),
                StringComparison.OrdinalIgnoreCase);

            return chosenIsTheInstall && (!Directory.Exists(install) || !Directory.EnumerateFileSystemEntries(install).Any())
                ? RepositoryPaths.NormalizedTarget(selection.Workload, chosen)
                : chosen;
        }
        catch (Exception)
        {
            return chosen;
        }
    }

    public void Cancel()
    {
        CancellationTokenSource? cts;
        lock (_gate) cts = _cts;
        cts?.Cancel();
    }

    /// <summary>
    /// Hands the orchestrator the current skip token. Read under the lock because
    /// <see cref="SkipCurrentDownload"/> swaps the source, and an unsynchronized read can observe
    /// the just-cancelled one and skip the next download too.
    /// </summary>
    private CancellationToken GetSkipDownloadToken()
    {
        lock (_gate) return _skipDownloadCts?.Token ?? CancellationToken.None;
    }

    public void SkipCurrentDownload()
    {
        CancellationTokenSource toCancel;

        lock (_gate)
        {
            if (_skipDownloadCts is null || _skipDownloadCts.IsCancellationRequested) return;

            toCancel = _skipDownloadCts;
            _skipDownloadCts = new CancellationTokenSource();
        }

        // Cancelled outside the lock on purpose: Cancel runs callbacks registered on the token
        // inline, and running foreign code while holding our lock is how deadlocks start.
        toCancel.Cancel();
        NotifyNow();
    }

    private void OnLog(InstallLogEntry entry)
    {
        Append(new InstallLogLine(entry.Timestamp, entry.Message, entry.Level));
        MarkDirty();
    }

    /// <summary>The one place lines enter the buffer, so the bound and the dropped-line count never diverge.</summary>
    private void Append(InstallLogLine line)
    {
        lock (_gate)
        {
            _log.Enqueue(line);
            while (_log.Count > MaxLogLines)
            {
                _log.Dequeue();
                _truncatedLogLines++;
            }
        }
    }

    private void OnStep(InstallationProgress progress)
    {
        Progress = progress;
        MarkDirty();
    }

    private void OnDownload(DownloadProgress progress)
    {
        CurrentDownload = progress;
        MarkDirty();
    }

    /// <summary>
    /// One report row, the moment the pipeline recorded it. Called on the installing thread, so it
    /// does the least it can: append, and mark the session dirty. Coalesced like log lines, because
    /// a model download's rows arrive in bursts and a render per row would ship the whole table
    /// over the SignalR circuit each time.
    /// </summary>
    private void OnReportRow(InstallReportEntry entry)
    {
        lock (_gate) _reportRows.Add(entry);
        MarkDirty();
    }

    /// <summary>Records that something changed without waking subscribers yet.</summary>
    private void MarkDirty() => Interlocked.Exchange(ref _dirty, 1);

    /// <summary>Timer tick: wake subscribers once if anything changed since the last tick.</summary>
    private void Flush()
    {
        if (Interlocked.Exchange(ref _dirty, 0) == 1)
            Changed?.Invoke();
    }

    /// <summary>Phase transitions bypass coalescing — those must never be a tick late.</summary>
    private void NotifyNow()
    {
        Interlocked.Exchange(ref _dirty, 0);
        Changed?.Invoke();
    }

    public void Dispose() => _flushTimer.Dispose();
}
