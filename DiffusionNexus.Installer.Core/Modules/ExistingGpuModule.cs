using DiffusionNexus.Installer.Core.Wizard;
using DiffusionNexus.Installer.SDK.Services.Hardware;

namespace DiffusionNexus.Installer.Core.Modules;

/// <summary>
/// The update path's GPU check: a warning, never a question. GpuPreflightModule's CPU consent would
/// replace torch and rewrite the launcher of a working AMD or CPU setup (the owner's decision,
/// 2026-11-03). An inconclusive probe counts as usable, as it does there.
/// </summary>
public sealed class ExistingGpuModule(IGpuDetectionService gpuDetection) : IWizardModule
{
    private WizardSelection? _selection;
    private GpuDetectionState _state = GpuDetectionState.Unknown;

    public string Id => "existing-gpu";
    public WizardStage Stage => WizardStage.System;
    public int Order => 0;
    public WorkloadCapability Satisfies => WorkloadCapability.None;

    public bool UsedIn(WizardMode mode) => mode == WizardMode.UpdateExisting;

    private bool NoUsableGpu => _state is GpuDetectionState.NoNvidiaGpu or GpuDetectionState.NvidiaGpuWithoutDriver;

    /// <summary>Update only installs no workload, so there's nothing to warn about.</summary>
    public bool AppliesTo(WizardSelection selection) => NoUsableGpu && !selection.UpdateOnly;

    public async Task InitializeAsync(WizardSelection selection, CancellationToken ct = default)
    {
        _selection = selection;
        var result = await gpuDetection.DetectAsync(ct).ConfigureAwait(false);
        Apply(result.State);
    }

    /// <summary>A driver may have been installed during the trip. An unsure probe keeps the earlier answer.</summary>
    public async Task RefreshAfterResumeAsync(CancellationToken ct = default)
    {
        var result = await gpuDetection.DetectAsync(ct).ConfigureAwait(false);
        if (result.State != GpuDetectionState.Unknown) Apply(result.State);
    }

    private void Apply(GpuDetectionState state)
    {
        _state = state;
        if (_selection?.Existing is { } run) run.GpuUsable = !NoUsableGpu;
    }

    public void Contribute(InstallationOptionsDraft draft) { }

    /// <summary>Never blocks: torch and the launcher simply stay as they are.</summary>
    public ModuleValidation Validate() => ModuleValidation.Ok();
}
