using System.Text.Json;
using DiffusionNexus.Installer.Core.Install;
using DiffusionNexus.Installer.SDK.Catalog;
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
    private readonly ICatalog _catalog;
    private readonly CatalogOptions _options;
    private readonly IUserSettingsRepository _settings;
    private readonly IInstallSession _session;
    private readonly Func<string?> _readEnvironment;
    // What this app shows (#53): every check and switch preview is filtered for it, so the page
    // announces only changes to installer workloads. The catalog itself is shared with the
    // DiffusionNexus app and always applied whole.
    private readonly CatalogAudience _audience;
    private readonly ILogger<CatalogUpdateCoordinator> _logger;

    // One lock for the state the UI reads; the in-flight task is the concurrency guard.
    private readonly Lock _gate = new();
    private Task? _inFlight;
    private bool _channelResolved;
    private bool _installRunning;

    // True while a channel switch is inside its awaits (settings, preview, save). Blocks CheckAsync
    // and ApplyAsync so a check cannot straddle them -- see the "channel switch" tests for the race
    // this closes. A switch waiting for Switch or Keep is PendingSwitch, not this: the time a person
    // takes to answer is never spent holding a flag that a missed release would leave stuck.
    private bool _switching;

    // A switch in this process whose content did not land: the channel the content was from when
    // it began, and the channel switched to. Consulted only for a section known by nothing else (a
    // state written before SDK 2.1.0 records no channel per section). Cleared by a landed apply, a
    // current check, or a switch back to the channel the content never left (PR #43 review).
    private (CatalogChannel From, CatalogChannel To)? _notLanded;

    // Which content a check found current, per channel; kept across restarts (PR #43 review).
    private readonly CatalogChannelConfirmations _confirmations;

    // "Apply catalog updates automatically" (#36): latched once read, like the channel, and on
    // every save -- so a read that started before a save cannot put the old value back.
    private bool _autoApplyResolved;

    // True while SetAutoApplyAsync saves. A channel switch writes the same file, so each refuses
    // while the other is inside its awaits.
    private bool _savingAutoApply;

    // Whether the apply in LastApply (or running) was started by the startup check.
    private bool _applyAutomatic;

    // Whether the SDK reads an override folder, probed with Installed (each check, apply, switch)
    // rather than on every read of SwitchIncompleteReason, which a page does on each render.
    private bool _overrideActive;

    public CatalogUpdateCoordinator(
        ICatalogUpdateService updates,
        ICatalog catalog,
        CatalogOptions options,
        IUserSettingsRepository settings,
        IInstallSession session,
        Func<string?> readEnvironment,
        CatalogAudience audience,
        ILogger<CatalogUpdateCoordinator>? logger = null,
        CatalogChannelConfirmations? confirmations = null)
    {
        _confirmations = confirmations ?? new CatalogChannelConfirmations(null, logger);
        _updates = updates;
        _catalog = catalog;
        _options = options;
        _settings = settings;
        _session = session;
        _readEnvironment = readEnvironment;
        _audience = audience;
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
    public long ContentGeneration => Interlocked.Read(ref _contentGeneration);
    private long _contentGeneration;

    // The LastApply instance whose Applied could not be read back; compared by reference, so every
    // write of LastApply clears it without each having to.
    private CatalogApplyResult? _uncertainApply;
    public bool LastApplyUncertain { get { lock (_gate) return LastApply is not null && ReferenceEquals(LastApply, _uncertainApply); } }

    public bool LastApplyFailed { get { lock (_gate) return LastApply is { } r && (r.Error is not null || r.Failed != CatalogSections.None); } }

    public bool AutoApply { get; private set; }

    public bool LastApplyAutomatic { get { lock (_gate) return _applyAutomatic && (LastApply is not null || Phase is CatalogUpdatePhase.Applying or CatalogUpdatePhase.Applied); } }

    // Not while a switch waits for an answer: the last check describes the channel being left,
    // and its update could not be applied until Switch or Keep (PR #43 review).
    // Announced only when the release changes something the installer shows (#53): a release that
    // changes the other app's workloads alone is applied silently or not at all, never offered --
    // unless it completes a switch that did not land: then it is what the user asked for, whoever
    // its changes are for, and Apply is the only way out (#53 review).
    // The rule is CatalogUpdateOffer's (shared with the page tests' stub). One read of LastCheck: a
    // render thread evaluates this while a switch may set it to null.
    public bool UpdateAvailable => CatalogUpdateOffer.Offered(LastCheck, Phase, PendingSwitch is not null, LastApplyFailed, () => SwitchIncompleteReason is not null);

    /// <summary>The followed channel's folder is behind its release, whoever the changes are for.</summary>
    private bool ReleaseAvailable => CatalogUpdateOffer.ReleaseAvailable(LastCheck, Phase, PendingSwitch is not null, LastApplyFailed);

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
            if (_switching || PendingSwitch is not null)
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
            _applyAutomatic = false;
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

            var check = (await _updates.CheckAsync(ct).ConfigureAwait(false)).For(_audience);
            var installed = LocalCatalogState.Load(_options.InstalledCatalogPath);
            var overrideActive = InstalledCatalogDescription.OverrideActive(_options);

            bool current;
            lock (_gate)
            {
                LastCheck = check;
                // An empty state (a channel folder that does not exist yet) describes nothing; the last known one stays.
                if (installed.RecordsAnything() || Installed is null) Installed = installed;
                _overrideActive = overrideActive;
                Phase = CatalogUpdatePhase.Checked;
                current = check.Outcome == CatalogUpdateOutcome.UpToDate && check.Channel == Channel;
                if (current) _notLanded = null;
            }
            if (current) _confirmations.Confirm(check.Channel, installed, otherFoldersUntouched: _options.UsesSharedFolders);
            _logger.LogInformation("Catalog update check: {Outcome} (remote v{Remote}, installed v{Installed}, {Workloads} workload / {Workflows} workflow changes for the installer, {All} in the release){Error}",
                check.Outcome, check.Remote?.CatalogVersion, installed.HighestCatalogVersion, check.Workloads.Count, check.Workflows.Count,
                check.Unfiltered!.Workloads.Count + check.Unfiltered.Workflows.Count,
                check.Error is null ? string.Empty : ": " + check.Error);
        }
        catch (Exception ex)
        {
            // The SDK's CheckAsync never throws and Load never throws, so this is the last line of
            // defence for a background task nobody awaits: report, never crash.
            _logger.LogError(ex, "Catalog update check failed unexpectedly");
            lock (_gate)
            {
                LastCheck = FailedCheck(Channel, Installed, ex.Message);
                Phase = CatalogUpdatePhase.Checked;
            }
        }
        Raise();
    }

    public Task ApplyAsync(CancellationToken ct = default) => StartApply(automatic: false, ct);

    public async Task CheckAtStartupAsync(CancellationToken ct = default)
    {
        await CheckAsync(ct).ConfigureAwait(false);

        bool wanted;
        lock (_gate) wanted = AutoApply && ReleaseAvailable;
        if (!wanted) return;

        // Not awaited, and not under the caller's token: this returns once the download has
        // started, and the apply reports through Changed like a manual one. StartApply decides
        // under the lock and logs why it refuses, so a running install, a manual click or a second
        // check in between wins and the log says which. A refusal is not retried later: the banner
        // and the Apply button wait, as they do with the setting off.
        _ = StartApply(automatic: true, CancellationToken.None);
    }

    private Task StartApply(bool automatic, CancellationToken ct)
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
                _logger.LogInformation("Catalog apply refused: an apply or check is already in flight, automatic {Automatic}", automatic);
                return Task.CompletedTask;
            }
            // A click applies what the page offered (CanApply). The startup check applies the release
            // whether or not it changes anything for the installer: full snapshots, and the folder is
            // shared with the other app (#53).
            var canStart = automatic
                ? ReleaseAvailable && Phase == CatalogUpdatePhase.Checked && !InstallRunning
                : CanApply;
            if (_switching || !canStart)
            {
                _logger.LogInformation("Catalog apply refused: phase {Phase}, release available {Release}, for the installer {Available}, install running {Running}, switching {Switching}, automatic {Automatic}",
                    Phase, ReleaseAvailable, UpdateAvailable, InstallRunning, _switching, automatic);
                return Task.CompletedTask;
            }
            if (automatic) _logger.LogInformation("Applying the catalog update automatically (setting on)");
            check = LastCheck!;
            Phase = CatalogUpdatePhase.Applying;
            LastApply = null;
            _applyAutomatic = automatic;
            Progress = null;
            inFlight = _inFlight = Task.Run(() => ApplyCoreAsync(check, ct), CancellationToken.None);
        }
        Raise();
        return inFlight;
    }

    private async Task ApplyCoreAsync(CatalogUpdateCheck check, CancellationToken ct)
    {
        _logger.LogInformation("Catalog apply started: v{Version} from {Channel}, both sections", check.Remote?.CatalogVersion, check.Channel);

        // What was installed before, to tell what landed if the SDK throws part-way (below).
        var before = TryReadState();
        try
        {
            var result = await _updates.ApplyAsync(check, CatalogSections.All, new ProgressRelay(this), ct).ConfigureAwait(false);
            // The state file, as before this PR: the SDK's own check seeds a channel folder before it
            // applies, so the file is there, and ICatalog.State here would force the whole catalog to
            // reload inside the apply (the SDK has just invalidated it).
            var installed = LocalCatalogState.Load(_options.InstalledCatalogPath);
            var overrideActive = InstalledCatalogDescription.OverrideActive(_options);
            var succeeded = result.Failed == CatalogSections.None && result.Error is null;
            var landedSomething = result.Applied != CatalogSections.None;

            // What the folder holds is current on this channel, whatever its sections record (an
            // embedded seed in a channel folder records the embedded manifest's channel). Before
            // the phase flips: a reader that waits for Applying to end must find it recorded.
            if (succeeded) _confirmations.Confirm(check.Channel, installed, otherFoldersUntouched: _options.UsesSharedFolders);
            var fresh = await AfterSwitchAsync(check, result.Applied, succeeded, installed).ConfigureAwait(false);

            lock (_gate)
            {
                LastApply = result;
                // An empty state (a folder the SDK could not fill at all) describes nothing; the last known one stays.
                if (installed.RecordsAnything() || Installed is null) Installed = installed;
                _overrideActive = overrideActive;
                Progress = null;
                if (fresh is not null) LastCheck = fresh;
                if (landedSomething) Interlocked.Increment(ref _contentGeneration);
                // A switch whose target folder was already current applies nothing: nothing was "updated" (#53).
                Phase = succeeded && landedSomething ? CatalogUpdatePhase.Applied : CatalogUpdatePhase.Checked;
                if (succeeded) _notLanded = null;
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

            // The SDK catches only I/O errors per section; anything else escapes after an earlier
            // section may already have been swapped in, and then the SDK has not invalidated its
            // catalog cache either. The applier records each section in the same state write as
            // its swap, so the state file says what landed. Unreadable: assume something did.
            var after = TryReadState();
            CatalogSections? landed = before is null || after is null ? null : LandedBetween(before, after);
            if (landed != CatalogSections.None) TryInvalidate();

            // Both swapped in before the throw: the update is installed, so it is no longer offered.
            var allLanded = landed == CatalogSections.All;

            var result = new CatalogApplyResult(landed ?? CatalogSections.None, CatalogSections.All & ~(landed ?? CatalogSections.None), ex.Message);
            var fresh = await AfterSwitchAsync(check, landed, succeeded: false, after).ConfigureAwait(false);
            lock (_gate)
            {
                if (landed != CatalogSections.None) Interlocked.Increment(ref _contentGeneration);
                if (after is not null) Installed = after;
                if (fresh is not null) LastCheck = fresh;
                LastApply = result;
                _uncertainApply = landed is null ? result : null;
                Progress = null;
                Phase = allLanded ? CatalogUpdatePhase.Applied : CatalogUpdatePhase.Checked;
                if (allLanded) _notLanded = null;
            }
            _logger.LogInformation("Catalog apply after the failure: landed={Landed}", landed?.ToString() ?? "unknown");
        }
        Raise();
    }

    /// <summary>
    /// What <see cref="LastCheck"/> becomes after the apply of a switch preview, or null to keep it. The
    /// preview compared the target's release with the folder being left; the SDK's apply checked the
    /// target folder itself and may have found the opposite. Landed everything: the preview stands (it
    /// names the landed release; the SDK just checked that folder). Landed nothing without an error:
    /// the SDK's verdict that the folder is current, recorded as an up-to-date check without a second
    /// fetch. Failed with nothing landed, or with a state that cannot be read back: the preview stands
    /// too -- applying it again is the retry (the SDK checks the target folder once more), the SDK
    /// already fetched and failed, so a second fetch would only double the error, and a fetch inside
    /// the apply's last-chance catch is nothing the user can act on. Landed part of the release (a
    /// throw between the sections): a check of the folder now followed tells what is left (#53 review).
    /// </summary>
    /// <param name="applied">What the apply landed; null when the state could not be read back.</param>
    private async Task<CatalogUpdateCheck?> AfterSwitchAsync(CatalogUpdateCheck check, CatalogSections? applied, bool succeeded, LocalCatalogState? installed)
    {
        if (!check.IsSwitchPreview || applied == CatalogSections.All) return null;
        if (succeeded && applied == CatalogSections.None)
            return new CatalogUpdateCheck(CatalogUpdateOutcome.UpToDate, check.Channel, check.Remote, installed ?? new LocalCatalogState(), [], [], null).For(_audience);
        if (applied is null or CatalogSections.None) return null;
        return await CheckAfterSwitchAsync().ConfigureAwait(false);
    }

    /// <summary>
    /// After a switch that started no apply (see CommitSwitchAsync), in the background: the state of
    /// the catalog the app reads now; with one folder and the same content, the confirmation; held by a
    /// running install, the check of the folder now followed -- the preview compared against the folder
    /// being left and says nothing about this one -- so the Apply after the install lands what the folder
    /// needs, or the page says it is current. Never throws.
    /// </summary>
    private async Task AfterSwitchNotAppliedAsync(CatalogChannel target, bool heldByInstall, bool confirm)
    {
        var state = await StateOfReadCatalogAsync().ConfigureAwait(false);
        lock (_gate)
        {
            // An empty state (the catalog could not be read and the folder is not seeded yet)
            // describes nothing the app will read; the last known state stays (#53 review round 3).
            if (state.RecordsAnything() || Installed is null) Installed = state;
        }
        if (confirm) _confirmations.Confirm(target, state, otherFoldersUntouched: false);

        if (heldByInstall)
        {
            var fresh = await CheckAfterSwitchAsync().ConfigureAwait(false);
            var current = fresh.Outcome == CatalogUpdateOutcome.UpToDate;
            lock (_gate)
            {
                LastCheck = fresh;
                if (current) _notLanded = null;
                Phase = CatalogUpdatePhase.Checked;
            }
            if (current) _confirmations.Confirm(target, state, otherFoldersUntouched: _options.UsesSharedFolders);
        }
        Raise();
    }

    /// <summary>A check of the folder now followed, after a switch. Never throws; a check that throws is a Failed check.</summary>
    private async Task<CatalogUpdateCheck> CheckAfterSwitchAsync()
    {
        try
        {
            var fresh = (await _updates.CheckAsync(CancellationToken.None).ConfigureAwait(false)).For(_audience);
            _logger.LogInformation("Catalog check after the switch: {Outcome} (remote v{Remote}, {Workloads} workload / {Workflows} workflow changes for the installer){Error}",
                fresh.Outcome, fresh.Remote?.CatalogVersion, fresh.Workloads.Count, fresh.Workflows.Count,
                fresh.Error is null ? string.Empty : ": " + fresh.Error);
            return fresh;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Catalog check after the switch failed unexpectedly");
            CatalogChannel channel;
            LocalCatalogState? installed;
            lock (_gate) (channel, installed) = (Channel, Installed);
            return FailedCheck(channel, installed, ex.Message);
        }
    }

    /// <summary>A check that never ran, filtered like every other so <see cref="LastCheck"/> always carries the audience.</summary>
    private CatalogUpdateCheck FailedCheck(CatalogChannel channel, LocalCatalogState? installed, string error) =>
        new CatalogUpdateCheck(CatalogUpdateOutcome.Failed, channel, null, installed ?? new LocalCatalogState(), [], [], error).For(_audience);

    /// <summary>
    /// The state of the catalog the app reads now. Through <see cref="ICatalog"/> on purpose: with
    /// SDK 2.3.0 shared folders a channel folder that does not exist yet is seeded on the first read,
    /// and the row must describe that seed, not an empty folder (#53 review). Falls back to the file
    /// when the catalog cannot load (a test's mock, a locked folder). Never throws.
    /// </summary>
    private async Task<LocalCatalogState> StateOfReadCatalogAsync()
    {
        var state = await CatalogStateReads.TryStateAsync(_catalog).ConfigureAwait(false);
        if (state is not null) return state;
        _logger.LogWarning("The catalog could not be read for its installed state; reading the state file instead");
        return LocalCatalogState.Load(_options.InstalledCatalogPath);
    }

    // Both never throw: they run around and inside the apply's last-chance catch, and an escape
    // there would leave the phase on Applying with no Raise -- every later check and apply refused
    // until a restart (no update deadlock, ever). Read handles only JsonException itself.
    private LocalCatalogState? TryReadState()
    {
        try
        {
            return LocalCatalogState.Read(_options.InstalledCatalogPath);
        }
        catch (Exception ex)
        {
            _logger.LogWarning("Catalog state could not be read: {Message}", ex.Message);
            return null;
        }
    }

    private void TryInvalidate()
    {
        try
        {
            _catalog.Invalidate();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Catalog cache could not be invalidated after a failed apply");
        }
    }

    // SectionState is a record: any field the applier writes (version, commit, time, origin) differs.
    private static CatalogSections LandedBetween(LocalCatalogState before, LocalCatalogState after) =>
        (Landed(before.Workloads, after.Workloads) ? CatalogSections.Workloads : CatalogSections.None)
        | (Landed(before.Workflows, after.Workflows) ? CatalogSections.Workflows : CatalogSections.None);

    // A section the SDK seeded from the embedded catalog on the way (its check of a channel folder that
    // did not exist yet seeds it before the download) is the SDK's, not this apply's.
    private static bool Landed(SectionState? before, SectionState? after) =>
        after != before && after?.Origin != SectionOrigin.Embedded;

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

    public bool SwitchIncomplete => SwitchIncompleteReason is not null;

    public string? SwitchIncompleteReason
    {
        get
        {
            LocalCatalogState? installed;
            CatalogUpdateCheck? check;
            CatalogChannel channel;
            CatalogChannel? notLandedTo;
            lock (_gate)
            {
                if (PendingSwitch is not null || _overrideActive) return null;
                (installed, check, channel, notLandedTo) = (Installed, LastCheck, Channel, _notLanded?.To);
            }

            // An override supersedes both channels.
            if (installed is null || check is { Outcome: CatalogUpdateOutcome.OverrideActive }) return null;

            // From catalog-state.json (each section's recorded channel) and the confirmations file,
            // so a restart does not forget that the content and the preference disagree; a section
            // known by neither falls back to this run's own switch that did not land.
            return InstalledCatalogDescription.Lagging(installed, channel,
                new CatalogSourceEvidence { Confirmed = _confirmations.Current, NotLandedTo = notLandedTo });
        }
    }

    public async Task SwitchChannelAsync(CatalogChannel target, CancellationToken ct = default)
    {
        CatalogUpdatePhase phaseBefore;
        lock (_gate)
        {
            if (_switching || _savingAutoApply || PendingSwitch is not null || Phase is CatalogUpdatePhase.Checking or CatalogUpdatePhase.Applying)
            {
                _logger.LogInformation("Catalog channel switch to {Channel} refused while {Phase}{Pending}",
                    target, Phase, PendingSwitch is null ? string.Empty : ", a switch to " + PendingSwitch.Target + " waiting for an answer");
                return;
            }
            // Claimed in the same critical section as the guard above so a check that starts during
            // the awaits below (settings, preview, save) sees _switching and backs off, instead of
            // resolving the channel this method is about to change out from under it. Held for the
            // awaits only and released by the finally on every path; a switch waiting for an
            // answer is PendingSwitch, which the guards test on their own.
            _switching = true;
            phaseBefore = Phase;
        }

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

            var preview = await PreviewAsync(current, target, ct).ConfigureAwait(false);
            lock (_gate) Phase = phaseBefore;

            if (ChannelSwitchWarning.For(preview) is { } warning)
            {
                lock (_gate) PendingSwitch = new CatalogChannelSwitch(preview, warning);
                _logger.LogInformation("Catalog channel switch to {Channel} waits for an answer: v{Version} removes {Removed} and {Changes}",
                    target, warning.Version, warning.Removed.Count, warning.ChangesText ?? "changes nothing");
                return;
            }

            await CommitSwitchAsync(preview, phaseBefore, ct).ConfigureAwait(false);
        }
        finally
        {
            lock (_gate)
            {
                _switching = false;
                // The preview's Checking ends here; a held switch's background check keeps its own.
                if (Phase == CatalogUpdatePhase.Checking && _inFlight is not { IsCompleted: false }) Phase = phaseBefore;
            }
            Raise();
        }
    }

    public async Task ConfirmSwitchAsync(CancellationToken ct = default)
    {
        CatalogChannelSwitch pending;
        CatalogUpdatePhase phaseBefore;
        lock (_gate)
        {
            if (PendingSwitch is null || _switching)
            {
                _logger.LogInformation("Catalog channel switch confirmed with no switch waiting; nothing to do");
                return;
            }
            pending = PendingSwitch;
            PendingSwitch = null;
            _switching = true;
            phaseBefore = Phase;
        }

        _logger.LogInformation("Catalog channel switch to {Channel} confirmed", pending.Target);
        try
        {
            await CommitSwitchAsync(pending.Preview, phaseBefore, ct).ConfigureAwait(false);
        }
        finally
        {
            lock (_gate) _switching = false;
            Raise();
        }
    }

    public void KeepChannel()
    {
        lock (_gate)
        {
            if (PendingSwitch is null) return;
            _logger.LogInformation("Catalog channel switch to {Channel} declined; nothing was saved", PendingSwitch.Target);
            PendingSwitch = null;
        }
        Raise();
    }

    /// <summary>The switch from <paramref name="from"/> to <paramref name="target"/>, as the SDK diffs it, filtered for the installer. Leaves <see cref="CatalogOptions.Channel"/> alone. Never throws.</summary>
    private async Task<CatalogUpdateCheck> PreviewAsync(CatalogChannel from, CatalogChannel target, CancellationToken ct)
    {
        _logger.LogInformation("Catalog channel switch: previewing {Channel}", target);
        try
        {
            var check = (await _updates.PreviewSwitchAsync(from, target, ct).ConfigureAwait(false)).For(_audience);
            _logger.LogInformation("Catalog channel preview: {Outcome} (remote v{Remote}, {Workloads} workload / {Workflows} workflow changes for the installer){Error}",
                check.Outcome, check.Remote?.CatalogVersion, check.Workloads.Count, check.Workflows.Count,
                check.Error is null ? string.Empty : ": " + check.Error);
            return check;
        }
        catch (Exception ex)
        {
            // The SDK's check never throws; this keeps a broken one from ending the switch without
            // the preference being saved -- a failed preview falls through like any other.
            _logger.LogError(ex, "Catalog channel preview failed unexpectedly");
            return FailedCheck(target, LocalCatalogState.Load(_options.InstalledCatalogPath), ex.Message);
        }
    }

    /// <summary>
    /// Saves the preference, then starts the apply of <paramref name="preview"/> when it has
    /// something to apply and no install runs, and returns: the choice is made once it is saved,
    /// and the download reports through <see cref="Changed"/> like a manual apply. The caller holds
    /// <see cref="_switching"/>. Only a failed save throws, and then nothing was changed.
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
            lock (_gate) Phase = phaseBefore;
            throw;
        }

        // Saved before the download starts: a stalled or failed apply must not lose the choice
        // (rule 1), and the next check then offers the same diff with Apply.
        var overrideActive = InstalledCatalogDescription.OverrideActive(_options);
        // What a switch applies: the target's release when the preview found changes, and, with shared
        // folders, a same-content preview too -- the SDK's apply checks the target channel's own
        // folder, fills it when it is missing or behind, and applies nothing when it is current. With
        // one folder the preview compared against that very folder: same content means nothing to fill.
        var appliable = preview.Outcome == CatalogUpdateOutcome.UpdatesAvailable
            || (preview.Outcome == CatalogUpdateOutcome.UpToDate && _options.UsesSharedFolders);
        var sameContentOneFolder = preview.Outcome == CatalogUpdateOutcome.UpToDate && !_options.UsesSharedFolders;
        // The target folder's state file, outside the lock (the shared-folder read enumerates the
        // format folders). Empty for a channel folder that does not exist yet: then the background
        // read below describes the seed, and Installed stays what it was until it does.
        var known = LocalCatalogState.Load(_options.ReadFolderFor(target));
        var applying = false;
        var heldByInstall = false;
        lock (_gate)
        {
            var left = Channel;
            ApplyResolution(_readEnvironment(), saved);
            _channelResolved = true;
            if (known.RecordsAnything()) Installed = known;
            _overrideActive = overrideActive;
            LastApply = null;
            _applyAutomatic = false;
            Progress = null;
            if (Channel != target)
            {
                // The variable is read live, so it can appear between the preview and here. It
                // decides, exactly as in SwitchChannelAsync.
                LastCheck = null;
                Phase = CatalogUpdatePhase.Idle;
            }
            else
            {
                LastCheck = preview;
                _notLanded = preview.Outcome == CatalogUpdateOutcome.OverrideActive || sameContentOneFolder ? null
                    // The content is still from where the first switch that did not land left it;
                    // switching back there is complete, whatever this preview answered.
                    : _notLanded is { From: var from } ? (from == target ? null : (from, target))
                    : (left, target);
                if (appliable && !InstallRunning)
                {
                    // Not under the caller's token: the switch has returned by the time the download
                    // runs, and a caller that cancels afterwards (a page going away) does not own it.
                    Phase = CatalogUpdatePhase.Applying;
                    _inFlight = Task.Run(() => ApplyCoreAsync(preview, CancellationToken.None), CancellationToken.None);
                    applying = true;
                }
                else
                {
                    // Failed, needs newer software, override: the installed content stays and the
                    // page says so. During an install the apply (or the retry) waits for it.
                    heldByInstall = appliable;
                    // Held by a running install: the folder now followed is checked below, in the
                    // background and under Checking, so the page offers nothing until it knows what
                    // that folder needs.
                    Phase = heldByInstall ? CatalogUpdatePhase.Checking : CatalogUpdatePhase.Checked;
                }
            }
        }
        if (!applying)
        {
            // One folder, same content: what the file records is what the SDK found current.
            var confirmed = sameContentOneFolder && known.RecordsAnything();
            if (confirmed) _confirmations.Confirm(target, known, otherFoldersUntouched: false);
            // Nothing will reload the state after an apply: the catalog the app reads now is read in the
            // background (reading it seeds a channel folder that does not exist yet, an extraction), so
            // this call returns once the choice is saved, as the contract says (#53 review rounds 3-5).
            lock (_gate) _inFlight = Task.Run(() => AfterSwitchNotAppliedAsync(target, heldByInstall, confirm: sameContentOneFolder && !confirmed), CancellationToken.None);
        }
        _logger.LogInformation("Catalog channel switched to {Channel}: {Next}", target,
            applying ? (preview.Outcome == CatalogUpdateOutcome.UpToDate ? "checking its folder" : "applying v" + preview.Remote?.CatalogVersion)
            : appliable ? "the apply waits for the running install"
            : preview.Outcome.ToString());
    }

    private async Task<string> SavePreferenceAsync(CatalogChannel channel, CancellationToken ct)
    {
        var settings = await _settings.GetOrCreateForCurrentUserAsync(ct).ConfigureAwait(false);
        settings.CatalogChannel = channel.ToString();
        await _settings.SaveAsync(settings, ct).ConfigureAwait(false);
        _logger.LogInformation("Catalog channel preference saved: {Channel}", channel);
        return settings.CatalogChannel;
    }

    public async Task<bool> SetAutoApplyAsync(bool on, CancellationToken ct = default)
    {
        lock (_gate)
        {
            // Refused like a channel switch, in the same phases: a check or apply running with the
            // setting about to change would leave "was it automatic?" up to timing.
            if (_switching || _savingAutoApply || PendingSwitch is not null || Phase is CatalogUpdatePhase.Checking or CatalogUpdatePhase.Applying)
            {
                _logger.LogInformation("Apply catalog updates automatically: change to {State} refused while {Phase}{Busy}",
                    on ? "on" : "off", Phase,
                    _switching || PendingSwitch is not null ? ", a channel switch in progress" : _savingAutoApply ? ", a save in progress" : string.Empty);
                return false;
            }
            _savingAutoApply = true;
        }

        try
        {
            var settings = await _settings.GetOrCreateForCurrentUserAsync(ct).ConfigureAwait(false);
            settings.ApplyCatalogUpdatesAutomatically = on;
            await _settings.SaveAsync(settings, ct).ConfigureAwait(false);
            lock (_gate)
            {
                AutoApply = on;
                _autoApplyResolved = true;
            }
            _logger.LogInformation("Apply catalog updates automatically: saved {State}", on ? "on" : "off");
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Apply catalog updates automatically: {State} could not be saved; nothing was changed", on ? "on" : "off");
            throw;
        }
        finally
        {
            lock (_gate) _savingAutoApply = false;
            Raise();
        }
    }

    public async Task<CatalogChannel> ResolveChannelAsync(CancellationToken ct = default)
    {
        var generationBefore = ContentGeneration;
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
        // A resolution that moved the app to another folder (shared folders) is a change the screens
        // built under the old one must hear about, like every other state change (#53 review round 3).
        if (ContentGeneration != generationBefore) Raise();
        lock (_gate) { return Channel; }
    }

    /// <summary>Reads the channel and the auto-apply setting, from the one settings file.</summary>
    private async Task EnsureChannelAsync(CancellationToken ct)
    {
        lock (_gate) { if (_channelResolved && _autoApplyResolved) return; }

        string? saved = null;
        var autoApply = false;
        var readSucceeded = true;
        try
        {
            var settings = await _settings.GetOrCreateForCurrentUserAsync(ct).ConfigureAwait(false);
            saved = settings.CatalogChannel;
            autoApply = settings.ApplyCatalogUpdatesAutomatically;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            // Not "following Stable": a channel a switch already latched stays. The line below
            // (ApplyResolution) names the channel when this read decides it.
            _logger.LogWarning(ex, "User settings could not be read; read again on the next check, switch or app update check. A startup check that cannot read them applies nothing by itself");
            readSucceeded = false;
        }

        lock (_gate)
        {
            // Unreadable: off, the default, until a read succeeds -- an unknown setting never
            // applies anything by itself. A save in the meantime is newer than this read.
            if (!_autoApplyResolved)
            {
                AutoApply = autoApply;
                if (readSucceeded) _autoApplyResolved = true;
            }

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
        // With one folder per channel the app reads another catalog from here on, downloaded or
        // not -- after a switch, and after a resolution that lands on a channel other than the
        // default a screen was built under. A screen built from the old one is stale (#32).
        // The channel first, then the bump: a page that captures the new generation and reads the catalog
        // in between must get the new folder's content, or it would stamp the old content as current.
        var folderChanges = _options.UsesSharedFolders && _options.Channel != channel;
        _options.Channel = channel;
        if (folderChanges) Interlocked.Increment(ref _contentGeneration);
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
