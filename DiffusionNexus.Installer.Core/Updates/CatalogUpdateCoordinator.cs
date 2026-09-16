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
        lock (_gate)
        {
            if (_inFlight is { IsCompleted: false }) return _inFlight;
            Phase = CatalogUpdatePhase.Checking;
            Progress = null;
            _inFlight = Task.Run(() => CheckCoreAsync(ct), CancellationToken.None);
        }
        Raise();
        return _inFlight;
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
        lock (_gate)
        {
            if (!CanApply)
            {
                _logger.LogInformation("Catalog apply refused: phase {Phase}, update available {Available}, install running {Running}",
                    Phase, UpdateAvailable, InstallRunning);
                return _inFlight is { IsCompleted: false } ? _inFlight : Task.CompletedTask;
            }
        }
        // Task 5 replaces this with the download + swap.
        return Task.CompletedTask;
    }

    public async Task SetChannelAsync(CatalogChannel channel, CancellationToken ct = default)
    {
        lock (_gate)
        {
            if (Phase is CatalogUpdatePhase.Checking or CatalogUpdatePhase.Applying)
            {
                _logger.LogInformation("Catalog channel change to {Channel} refused while {Phase}", channel, Phase);
                return;
            }
        }

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
        Raise();
    }

    private async Task EnsureChannelAsync(CancellationToken ct)
    {
        lock (_gate) { if (_channelResolved) return; }

        string? saved = null;
        try
        {
            saved = (await _settings.GetOrCreateForCurrentUserAsync(ct).ConfigureAwait(false)).CatalogChannel;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            _logger.LogWarning(ex, "User settings could not be read; following the Stable catalog channel");
        }

        lock (_gate)
        {
            ApplyResolution(_readEnvironment(), saved);
            _channelResolved = true;
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

    private void Raise() => Changed?.Invoke();

    public void Dispose() => _session.Changed -= OnSessionChanged;
}
