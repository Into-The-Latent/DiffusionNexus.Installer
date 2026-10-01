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

    // True from the moment SwitchChannelAsync passes its guard until the switch is done (saved,
    // failed or declined), including while it waits for Switch or Keep. Blocks CheckAsync and
    // ApplyAsync so a check cannot straddle a switch's own awaits -- see the "channel switch"
    // tests for the race this closes.
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
            if (_inFlight is { IsCompleted: false })
            {
                // A running check is joined. A running apply is not: handing its task back would
                // hold the caller ("Check for updates" sitting on "Checking...") for the length
                // of the whole download -- the same trap ApplyAsync's refusals avoid below.
                if (Phase != CatalogUpdatePhase.Applying) return _inFlight;
                _logger.LogInformation("Catalog check refused: an apply is in progress");
                return Task.CompletedTask;
            }
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
                // The installed catalog is what the followed channel serves (or an override
                // supersedes both): whatever a switch left undone is done.
                if (check.Outcome is CatalogUpdateOutcome.UpToDate or CatalogUpdateOutcome.OverrideActive) SwitchIncomplete = false;
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
                if (succeeded) SwitchIncomplete = false;
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
        private long? _lastShown;

        public void Report(CatalogDownloadProgress value)
        {
            // Only when what a page can show moves: the SDK reports on every ~80 KB read, and
            // every page that shows the catalog state would re-render on each one for nothing
            // (same reasoning as OnSessionChanged).
            var shown = Shown(value);
            bool moved;
            lock (owner._gate)
            {
                owner.Progress = value;
                moved = shown != _lastShown;
                _lastShown = shown;
            }
            if (moved) owner.Raise();
        }

        /// <summary>The resolution the pages display at: a whole percent, or 0.1 MB when the total is unknown.</summary>
        private static long Shown(CatalogDownloadProgress value) => value.TotalBytes is > 0
            ? (long)Math.Round(100.0 * value.BytesReceived / value.TotalBytes.Value)
            : (long)Math.Round(value.BytesReceived / 104_857.6);
    }

    public CatalogChannelSwitch? PendingSwitch { get; private set; }
    public bool SwitchIncomplete { get; private set; }

    public async Task SwitchChannelAsync(CatalogChannel target, CancellationToken ct = default)
    {
        CatalogUpdatePhase phaseBefore;
        lock (_gate)
        {
            if (_switching || Phase is CatalogUpdatePhase.Checking or CatalogUpdatePhase.Applying)
            {
                _logger.LogInformation("Catalog channel switch to {Channel} refused while {Phase}{Pending}",
                    target, Phase, PendingSwitch is null ? string.Empty : ", a switch to " + PendingSwitch.Target + " waiting for an answer");
                return;
            }
            // Claimed in the same critical section as the guard above so a check that starts during
            // the awaits below (settings, preview, save) sees _switching and backs off, instead of
            // resolving the channel this method is about to change out from under it. Held until
            // the switch is done or, when it waits for an answer, until Switch or Keep.
            _switching = true;
            phaseBefore = Phase;
        }

        var handedOver = false;
        try
        {
            await EnsureChannelAsync(ct).ConfigureAwait(false);
            CatalogChannel current;
            CatalogChannelSource source;
            lock (_gate) { (current, source) = (Channel, ChannelSource); }

            if (source == CatalogChannelSource.Environment)
            {
                // The environment decides what this run follows and installs. Previewing or applying
                // another channel would contradict it, so the preference is saved for the runs
                // without the variable, and nothing else changes.
                var saved = await SavePreferenceAsync(target, ct).ConfigureAwait(false);
                lock (_gate)
                {
                    ApplyResolution(_readEnvironment(), saved);
                    _channelResolved = true;
                }
                return;
            }

            if (current == target)
            {
                _logger.LogInformation("Catalog channel switch to {Channel}: already followed, nothing to do", target);
                return;
            }

            lock (_gate)
            {
                Phase = CatalogUpdatePhase.Checking;
                Progress = null;
            }
            Raise();

            var preview = await PreviewAsync(target, ct).ConfigureAwait(false);
            lock (_gate) Phase = phaseBefore;

            if (ChannelSwitchWarning.For(preview) is { } warning)
            {
                lock (_gate) PendingSwitch = new CatalogChannelSwitch(preview, warning);
                handedOver = true;
                _logger.LogInformation("Catalog channel switch to {Channel} waits for an answer: v{Version} removes {Removed} and {Changes}",
                    target, warning.Version, warning.Removed.Count, warning.ChangesText ?? "changes nothing");
                Raise();
                return;
            }

            handedOver = true;
            await CommitSwitchAsync(preview, phaseBefore, ct).ConfigureAwait(false);
        }
        finally
        {
            if (!handedOver)
            {
                lock (_gate)
                {
                    _switching = false;
                    if (Phase == CatalogUpdatePhase.Checking) Phase = phaseBefore;
                }
                Raise();
            }
        }
    }

    public Task ConfirmSwitchAsync(CancellationToken ct = default)
    {
        CatalogChannelSwitch pending;
        CatalogUpdatePhase phaseBefore;
        lock (_gate)
        {
            if (PendingSwitch is null)
            {
                _logger.LogInformation("Catalog channel switch confirmed with no switch waiting; nothing to do");
                return Task.CompletedTask;
            }
            pending = PendingSwitch;
            PendingSwitch = null;
            phaseBefore = Phase;
        }
        _logger.LogInformation("Catalog channel switch to {Channel} confirmed", pending.Target);
        return CommitSwitchAsync(pending.Preview, phaseBefore, ct);
    }

    public void KeepChannel()
    {
        lock (_gate)
        {
            if (PendingSwitch is null) return;
            _logger.LogInformation("Catalog channel switch to {Channel} declined; nothing was saved", PendingSwitch.Target);
            PendingSwitch = null;
            _switching = false;
        }
        Raise();
    }

    /// <summary>A check of <paramref name="target"/> that leaves <see cref="CatalogOptions.Channel"/> alone. Never throws.</summary>
    private async Task<CatalogUpdateCheck> PreviewAsync(CatalogChannel target, CancellationToken ct)
    {
        _logger.LogInformation("Catalog channel switch: previewing {Channel}", target);
        try
        {
            var check = await _updates.CheckAsync(target, ct).ConfigureAwait(false);
            _logger.LogInformation("Catalog channel preview: {Outcome} (remote v{Remote}, {Workloads} workload / {Workflows} workflow changes){Error}",
                check.Outcome, check.Remote?.CatalogVersion, check.Workloads.Count, check.Workflows.Count,
                check.Error is null ? string.Empty : ": " + check.Error);
            return check;
        }
        catch (Exception ex)
        {
            // The SDK's check never throws; this keeps a broken one from ending the switch without
            // the preference being saved -- a failed preview falls through like any other.
            _logger.LogError(ex, "Catalog channel preview failed unexpectedly");
            return new CatalogUpdateCheck(CatalogUpdateOutcome.Failed, target, null, LocalCatalogState.Load(_options.InstalledCatalogPath), [], [], ex.Message);
        }
    }

    /// <summary>
    /// Saves the preference, then applies <paramref name="preview"/> when it has something to apply
    /// and no install runs. The caller claimed <see cref="_switching"/>; this releases it on every
    /// path. Only a failed save throws, and then nothing was changed.
    /// </summary>
    private async Task CommitSwitchAsync(CatalogUpdateCheck preview, CatalogUpdatePhase phaseBefore, CancellationToken ct)
    {
        var target = preview.Channel;
        string saved;
        try
        {
            saved = await SavePreferenceAsync(target, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Catalog channel {Channel} could not be saved; nothing was changed", target);
            lock (_gate)
            {
                _switching = false;
                Phase = phaseBefore;
            }
            Raise();
            throw;
        }

        // Saved before the download starts: a stalled or failed apply must not lose the choice
        // (rule 1), and the next check then offers the same diff with Apply.
        var installed = LocalCatalogState.Load(_options.InstalledCatalogPath);
        Task? apply = null;
        lock (_gate)
        {
            ApplyResolution(_readEnvironment(), saved);
            _channelResolved = true;
            Installed = installed;
            LastApply = null;
            Progress = null;
            if (Channel != target)
            {
                // The variable is read live, so it can appear between the preview and here. It
                // decides, exactly as in SwitchChannelAsync.
                LastCheck = null;
                SwitchIncomplete = false;
                Phase = CatalogUpdatePhase.Idle;
            }
            else
            {
                LastCheck = preview;
                SwitchIncomplete = preview.Outcome is not (CatalogUpdateOutcome.UpToDate or CatalogUpdateOutcome.OverrideActive);
                if (preview.Outcome == CatalogUpdateOutcome.UpdatesAvailable && !InstallRunning)
                {
                    Phase = CatalogUpdatePhase.Applying;
                    apply = _inFlight = Task.Run(() => ApplyCoreAsync(preview, ct), CancellationToken.None);
                }
                else
                {
                    // Up to date: same content, only the preference changes. Anything else keeps the
                    // installed content and says so; during an install the Apply button waits for it.
                    Phase = CatalogUpdatePhase.Checked;
                }
            }
            _switching = false;
        }
        _logger.LogInformation("Catalog channel switched to {Channel}: {Next}", target,
            apply is not null ? "applying v" + preview.Remote?.CatalogVersion
            : preview.Outcome == CatalogUpdateOutcome.UpdatesAvailable ? "the apply waits for the running install"
            : preview.Outcome.ToString());
        Raise();

        if (apply is not null) await apply.ConfigureAwait(false);
    }

    private async Task<string> SavePreferenceAsync(CatalogChannel channel, CancellationToken ct)
    {
        var settings = await _settings.GetOrCreateForCurrentUserAsync(ct).ConfigureAwait(false);
        settings.CatalogChannel = channel.ToString();
        await _settings.SaveAsync(settings, ct).ConfigureAwait(false);
        _logger.LogInformation("Catalog channel preference saved: {Channel}", channel);
        return settings.CatalogChannel;
    }

    public async Task<CatalogChannel> ResolveChannelAsync(CancellationToken ct = default)
    {
        try
        {
            await EnsureChannelAsync(ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // EnsureChannelAsync absorbs the failures a settings file is expected to have; this is
            // for everything else, because the caller is a fire-and-forget app update check.
            _logger.LogWarning(ex, "The update channel could not be resolved; answering {Channel}", Channel);
        }
        lock (_gate) { return Channel; }
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
            // A channel switch finished while the read above was awaited. Its answer is the newer
            // one; applying the value read before it would silently undo the switch.
            if (_channelResolved) return;

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
