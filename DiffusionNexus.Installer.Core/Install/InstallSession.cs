using DiffusionNexus.Installer.Core.Wizard;
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

    public IReadOnlyList<InstallLogLine> LogLines
    {
        get { lock (_gate) return [.. _log]; }
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
            Result = null;
            Progress = null;
            CurrentDownload = null;
            _log.Clear();
        }

        _cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _skipDownloadCts = new CancellationTokenSource();
        NotifyNow();
        _flushTimer.Change(_flushInterval, _flushInterval);

        try
        {
            var result = await _orchestrator.InstallAsync(
                plan.Selection.Workload,
                plan.Selection.TargetFolder,
                plan.ToOptions(),
                new InlineProgress<InstallLogEntry>(OnLog),
                new InlineProgress<InstallationProgress>(OnStep),
                new InlineProgress<DownloadProgress>(OnDownload),
                () => _skipDownloadCts!.Token,
                _cts.Token).ConfigureAwait(false);

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
            // sitting in the buffer waiting for a tick that no longer comes.
            _flushTimer.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
            NotifyNow();
        }
    }

    public void Cancel() => _cts?.Cancel();

    public void SkipCurrentDownload()
    {
        var cts = _skipDownloadCts;
        if (cts is null || cts.IsCancellationRequested) return;

        cts.Cancel();
        _skipDownloadCts = new CancellationTokenSource();
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
