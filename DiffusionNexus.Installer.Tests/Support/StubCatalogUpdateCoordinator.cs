using DiffusionNexus.Installer.Core.Updates;
using DiffusionNexus.Installer.SDK.Catalog.Packaging;
using DiffusionNexus.Installer.SDK.Catalog.Updates;

namespace DiffusionNexus.Installer.Tests.Support;

/// <summary>Settable coordinator for component tests: the fixture sets state, the component renders it.</summary>
internal sealed class StubCatalogUpdateCoordinator : ICatalogUpdateCoordinator
{
    public CatalogChannel Channel { get; set; } = CatalogChannel.Stable;
    public CatalogChannelSource ChannelSource { get; set; } = CatalogChannelSource.Default;
    public string? OverridePath { get; set; }
    public CatalogUpdatePhase Phase { get; set; } = CatalogUpdatePhase.Idle;
    public CatalogUpdateCheck? LastCheck { get; set; }
    public LocalCatalogState? Installed { get; set; }
    public CatalogDownloadProgress? Progress { get; set; }
    public CatalogApplyResult? LastApply { get; set; }
    public string? ApplyBlockedReason { get; set; }

    public bool UpdateAvailable => LastCheck?.Outcome == CatalogUpdateOutcome.UpdatesAvailable && Phase != CatalogUpdatePhase.Applied;
    public bool CanApply => UpdateAvailable && Phase == CatalogUpdatePhase.Checked && ApplyBlockedReason is null;

    public event Action? Changed;

    public int Checks { get; private set; }
    public int Applies { get; private set; }
    public int ChannelResolutions { get; private set; }
    public CatalogChannel? ChannelSet { get; private set; }
    public int Subscribers => Changed?.GetInvocationList().Length ?? 0;

    public Task CheckAsync(CancellationToken ct = default) { Checks++; return Task.CompletedTask; }
    public Task ApplyAsync(CancellationToken ct = default) { Applies++; return Task.CompletedTask; }
    public Task<CatalogChannel> ResolveChannelAsync(CancellationToken ct = default) { ChannelResolutions++; return Task.FromResult(Channel); }
    /// <summary>Mimics the real coordinator's silent refusal: no state change, no Changed.</summary>
    public bool RefuseChannelChange { get; set; }
    public Exception? ChannelSaveFailure { get; set; }

    public Task SetChannelAsync(CatalogChannel channel, CancellationToken ct = default)
    {
        ChannelSet = channel;
        if (ChannelSaveFailure is not null) return Task.FromException(ChannelSaveFailure);
        if (!RefuseChannelChange) Channel = channel;
        return Task.CompletedTask;
    }

    public void RaiseChanged() => Changed?.Invoke();
}
