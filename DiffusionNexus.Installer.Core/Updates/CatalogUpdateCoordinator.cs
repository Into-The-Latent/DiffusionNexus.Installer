using System.Text.Json;
using DiffusionNexus.Installer.Core.Install;
using DiffusionNexus.Installer.SDK.Catalog.Packaging;
using DiffusionNexus.Installer.SDK.Catalog.Updates;
using DiffusionNexus.Installer.SDK.Services.Settings;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace DiffusionNexus.Installer.Core.Updates;

/// <summary>
/// Owns the check/apply lifecycle around the SDK's <see cref="ICatalogUpdateService"/>. This is
/// the one place that writes <see cref="CatalogOptions.Channel"/>, which the SDK reads on every
/// check. Every step logs, so a stalled update shows its last successful step in the console.
/// </summary>
public sealed class CatalogUpdateCoordinator : ICatalogUpdateCoordinator, IDisposable
{
    private readonly ICatalogUpdateService _updates;
    private readonly CatalogOptions _options;
    private readonly IUserSettingsRepository _settings;
    private readonly IInstallSession _session;
    private readonly Func<string?> _readEnvironment;
    private readonly ILogger<CatalogUpdateCoordinator> _logger;

    // One lock for the state the UI reads; the in-flight task is the concurrency guard.
    private readonly Lock _gate = new();
    private Task? _inFlight;
    private bool _channelResolved;
    private bool _installRunning;

    // True from the moment SetChannelAsync passes its guard until its state mutation is done
    // (success or failure). Blocks CheckAsync/ApplyAsync so a check cannot straddle a channel
    // switch's own await -- see the "channel switch" tests for the race this closes.
    private bool _switching;

    public CatalogUpdateCoordinator(
        ICatalogUpdateService updates,
        CatalogOptions options,
        IUserSettingsRepository settings,
        IInstallSession session,
        Func<string?> readEnvironment,
        ILogger<CatalogUpdateCoordinator>? logger = null)
    {
        _updates = updates;
        _options = options;
        _settings = settings;
        _session = session;
        _readEnvironment = readEnvironment;
        _logger = logger ?? NullLogger<CatalogUpdateCoordinator>.Instance;

        _installRunning = session.Phase == InstallPhase.Running;
        _session.Changed += OnSessionChanged;
    }

    public CatalogChannel Channel { get; private set; } = CatalogChannel.Stable;
    public CatalogChannelSource ChannelSource { get; private set; } = CatalogChannelSource.Default;
    public string? OverridePath => _options.LocalOverridePath;
    public CatalogUpdatePhase Phase { get; private set; } = CatalogUpdatePhase.Idle;
    public CatalogUpdateCheck? LastCheck { get; private set; }
    public LocalCatalogState? Installed { get; private set; }
    public CatalogDownloadProgress? Progress { get; private set; }
    public CatalogApplyResult? LastApply { get; private set; }

    public bool UpdateAvailable =>
        LastCheck?.Outcome == CatalogUpdateOutcome.UpdatesAvailable && Phase != CatalogUpdatePhase.Applied;

    // Reads the session live rather than the cached flag: the flag only decides when to re-raise.
    public bool CanApply => UpdateAvailable && Phase == CatalogUpdatePhase.Checked && !InstallRunning;

    public string? ApplyBlockedReason => UpdateAvailable && InstallRunning
        ? $"It can be applied once {_session.Plan?.Selection.Workload.Name ?? "the current install"} has finished."
        : null;

    public event Action? Changed;

    private bool InstallRunning => _session.Phase == InstallPhase.Running;

    public Task CheckAsync(CancellationToken ct = default)
    {
        Task inFlight;
        lock (_gate)
        {
            if (_switching)
            {
                _logger.LogInformation("Catalog check refused: a channel switch is in progress");
                return Task.CompletedTask;
            }
            if (_inFlight is { IsCompleted: false }) return _inFlight;
            Phase = CatalogUpdatePhase.Checking;
            Progress = null;
            // A stale error from a previous failed apply must not survive into a fresh check --
            // the outcome line reads from LastCheck, but the apply-failure line below it reads
            // LastApply directly and would otherwise keep showing a retry banner for an apply
            // nobody has attempted since.
            LastApply = null;
            inFlight = _inFlight = Task.Run(() => CheckCoreAsync(ct), CancellationToken.None);
        }
        Raise();
        return inFlight;
    }

    private async Task CheckCoreAsync(CancellationToken ct)
    {
        try
        {
            await EnsureChannelAsync(ct).ConfigureAwait(false);
            _logger.LogInformation("Catalog update check started on {Channel}", Channel);

            var check = await _updates.CheckAsync(ct).ConfigureAwait(false);
            var installed = LocalCatalogState.Load(_options.InstalledCatalogPath);

            lock (_gate)
            {
                LastCheck = check;
                Installed = installed;
                Phase = CatalogUpdatePhase.Checked;
            }
            _logger.LogInformation("Catalog update check: {Outcome} (remote v{Remote}, installed v{Installed}, {Workloads} workload / {Workflows} workflow changes){Error}",
                check.Outcome, check.Remote?.CatalogVersion, installed.HighestCatalogVersion, check.Workloads.Count, check.Workflows.Count,
                check.Error is null ? string.Empty : ": " + check.Error);
        }
        catch (Exception ex)
        {
            // The SDK's CheckAsync never throws and Load never throws, so this is the last line of
            // defence for a background task nobody awaits: report, never crash.
            _logger.LogError(ex, "Catalog update check failed unexpectedly");
            lock (_gate)
            {
                LastCheck = new CatalogUpdateCheck(CatalogUpdateOutcome.Failed, Channel, null, Installed ?? new LocalCatalogState(), [], [], ex.Message);
                Phase = CatalogUpdatePhase.Checked;
            }
        }
        Raise();
    }

    public Task ApplyAsync(CancellationToken ct = default)
    {
        CatalogUpdateCheck check;
        Task inFlight;
        lock (_gate)
        {
            // Every refusal is a no-op, never a hand-back of the in-flight check: the documented
            // contract is "a no-op unless CanApply", and returning _inFlight would make a refused
            // apply block its caller for the length of an unrelated network check.
            if (_inFlight is { IsCompleted: false })
            {
                _logger.LogInformation("Catalog apply refused: an apply or check is already in flight");
                return Task.CompletedTask;
            }
            if (_switching || !CanApply)
            {
                _logger.LogInformation("Catalog apply refused: phase {Phase}, update available {Available}, install running {Running}, switching {Switching}",
                    Phase, UpdateAvailable, InstallRunning, _switching);
                return Task.CompletedTask;
            }
            check = LastCheck!;
            Phase = CatalogUpdatePhase.Applying;
            LastApply = null;
            Progress = null;
            inFlight = _inFlight = Task.Run(() => ApplyCoreAsync(check, ct), CancellationToken.None);
        }
        Raise();
        return inFlight;
    }

    private async Task ApplyCoreAsync(CatalogUpdateCheck check, CancellationToken ct)
    {
        _logger.LogInformation("Catalog apply started: v{Version} from {Channel}, both sections", check.Remote?.CatalogVersion, check.Channel);
        try
        {
            var result = await _updates.ApplyAsync(check, CatalogSections.All, new ProgressRelay(this), ct).ConfigureAwait(false);
            var installed = LocalCatalogState.Load(_options.InstalledCatalogPath);
            var succeeded = result.Failed == CatalogSections.None && result.Error is null;

            lock (_gate)
            {
                LastApply = result;
                Installed = installed;
                Progress = null;
                Phase = succeeded ? CatalogUpdatePhase.Applied : CatalogUpdatePhase.Checked;
            }
            _logger.LogInformation("Catalog apply finished: applied={Applied} failed={Failed} installed v{Installed}{Error}",
                result.Applied, result.Failed, installed.HighestCatalogVersion, result.Error is null ? string.Empty : ": " + result.Error);
        }
        catch (OperationCanceledException)
        {
            // The SDK lets a caller's cancel through on purpose; it is not a failure and is not shown as one.
            _logger.LogInformation("Catalog apply cancelled; nothing was changed");
            lock (_gate)
            {
                LastApply = null;
                Progress = null;
                Phase = CatalogUpdatePhase.Checked;
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Catalog apply failed unexpectedly");
            lock (_gate)
            {
                LastApply = new CatalogApplyResult(CatalogSections.None, CatalogSections.All, ex.Message);
                Progress = null;
                Phase = CatalogUpdatePhase.Checked;
            }
        }
        Raise();
    }

    /// <summary>
    /// Not <see cref="System.Progress{T}"/>: that posts to the captured SynchronizationContext, which
    /// on a pool thread means "whenever", and the page would render stale percentages out of order.
    /// </summary>
    private sealed class ProgressRelay(CatalogUpdateCoordinator owner) : IProgress<CatalogDownloadProgress>
    {
        public void Report(CatalogDownloadProgress value)
        {
            lock (owner._gate) { owner.Progress = value; }
            owner.Raise();
        }
    }

    public async Task SetChannelAsync(CatalogChannel channel, CancellationToken ct = default)
    {
        lock (_gate)
        {
            if (_switching || Phase is CatalogUpdatePhase.Checking or CatalogUpdatePhase.Applying)
            {
                _logger.LogInformation("Catalog channel change to {Channel} refused while {Phase}", channel, Phase);
                return;
            }
            // Claimed in the same critical section as the guard above so a check that starts during
            // the await below (GetOrCreateForCurrentUserAsync / SaveAsync) sees _switching and backs
            // off, instead of resolving the channel this method is about to change out from under it.
            _switching = true;
        }

        try
        {
            var settings = await _settings.GetOrCreateForCurrentUserAsync(ct).ConfigureAwait(false);
            settings.CatalogChannel = channel.ToString();
            await _settings.SaveAsync(settings, ct).ConfigureAwait(false);
            _logger.LogInformation("Catalog channel preference saved: {Channel}", channel);

            lock (_gate)
            {
                ApplyResolution(_readEnvironment(), settings.CatalogChannel);
                _channelResolved = true;
                LastCheck = null;
                LastApply = null;
                Progress = null;
                Phase = CatalogUpdatePhase.Idle;
            }
        }
        finally
        {
            lock (_gate) { _switching = false; }
        }
        Raise();
    }

    private async Task EnsureChannelAsync(CancellationToken ct)
    {
        lock (_gate) { if (_channelResolved) return; }

        string? saved = null;
        var readSucceeded = true;
        try
        {
            saved = (await _settings.GetOrCreateForCurrentUserAsync(ct).ConfigureAwait(false)).CatalogChannel;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            _logger.LogWarning(ex, "User settings could not be read; following the Stable catalog channel for this check");
            readSucceeded = false;
        }

        lock (_gate)
        {
            ApplyResolution(_readEnvironment(), saved);
            // Only a successful read latches the resolution: a transient failure (locked file,
            // momentary permission error) must not pin the process to Stable for its whole
            // lifetime -- the next check tries the read again.
            if (readSucceeded) _channelResolved = true;
        }
    }

    /// <summary>Caller holds <see cref="_gate"/>.</summary>
    private void ApplyResolution(string? environmentValue, string? savedValue)
    {
        var (channel, source) = CatalogChannelResolver.Resolve(environmentValue, savedValue);
        Channel = channel;
        ChannelSource = source;
        _options.Channel = channel;
        _logger.LogInformation("Following the {Channel} catalog channel ({Source})", channel, source);
    }

    private void OnSessionChanged()
    {
        var running = InstallRunning;
        bool flipped;
        lock (_gate)
        {
            flipped = running != _installRunning;
            _installRunning = running;
        }
        // Only on a flip: the session raises ~10x a second during an install, and every page
        // that shows the Apply button would re-render on each tick for nothing.
        if (flipped) Raise();
    }

    // Invokes each subscriber individually and swallows what it throws: a throwing handler (a
    // Blazor re-render against a disposed circuit is the realistic case) must not fault the
    // background check task and break the "CheckAsync never throws" contract.
    private void Raise()
    {
        var handlers = Changed;
        if (handlers is null) return;
        foreach (var handler in handlers.GetInvocationList())
        {
            try
            {
                ((Action)handler)();
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Catalog update coordinator: a Changed subscriber threw");
            }
        }
    }

    public void Dispose() => _session.Changed -= OnSessionChanged;
}
