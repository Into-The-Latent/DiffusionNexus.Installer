using DiffusionNexus.Installer.Core.Wizard;
using DiffusionNexus.Installer.SDK.Models.Installation;
using DiffusionNexus.Installer.SDK.Services;

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
    private readonly Lock _gate = new();
    private readonly Queue<InstallLogLine> _log = new();
    private readonly List<InstallReportEntry> _reportRows = [];
    private readonly Timer _flushTimer;
    private int _dirty;
    private CancellationTokenSource? _cts;
    private CancellationTokenSource? _skipDownloadCts;

    public InstallSession(IInstallationOrchestrator orchestrator, TimeSpan? flushInterval = null)
    {
        _orchestrator = orchestrator;
        _flushInterval = flushInterval ?? DefaultFlushInterval;
        _flushTimer = new Timer(_ => Flush(), null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
    }

    public InstallPhase Phase { get; private set; } = InstallPhase.Idle;
    public InstallationProgress? Progress { get; private set; }
    public DownloadProgress? CurrentDownload { get; private set; }
    public InstallationResult? Result { get; private set; }
    public WizardPlan? Plan { get; private set; }

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
            _log.Clear();
            _reportRows.Clear();
        }

        try
        {
            // Inside the try: a throw here used to escape uncaught and wedge Phase at Running.
            // Locked, like _skipDownloadCts below, so Cancel() reading _cts under the same gate can
            // never observe a torn or stale value -- see Cancel().
            lock (_gate) _cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            lock (_gate) _skipDownloadCts = new CancellationTokenSource();
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
                lock (_gate) { _reportRows.Clear(); _reportRows.AddRange(result.Report); }

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

            NotifyNow();
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
        lock (_gate)
        {
            _log.Enqueue(new InstallLogLine(entry.Timestamp, entry.Message, entry.Level));
            while (_log.Count > MaxLogLines) _log.Dequeue();
        }
        MarkDirty();
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
