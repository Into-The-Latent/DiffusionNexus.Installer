using DiffusionNexus.Installer.Core.Catalog;
using DiffusionNexus.Installer.Core.Wizard;

namespace DiffusionNexus.Installer.Core.Modules;

/// <summary>
/// Turns a workload's SelectedLamaCppWheelId into the wheel URL the install step needs.
/// <para>
/// Without this the pipeline still schedules InstallLlamaCpp — ComfyUIInstallationFlow keys on the
/// id, not on any flag the wizard sets — and LlamaCppInstallStepHandler then fails on a null
/// ResolvedLlamaCppWheelUrl, producing a red report row on an otherwise clean install. Resolving
/// the wheel here is what keeps such a workload offerable instead of gated out of the gallery.
/// </para>
/// </summary>
public sealed class LlamaCppModule(IWorkloadSource workloads) : IWizardModule
{
    private Guid? _wheelId;

    public string Id => "llama-cpp";
    public WizardStage Stage => WizardStage.System;
    public int Order => 50;
    public WorkloadCapability Satisfies => WorkloadCapability.LlamaCpp;

    /// <summary>Display name of the resolved wheel, or null when the id matched nothing.</summary>
    public string? WheelName { get; private set; }

    public string? WheelUrl { get; private set; }

    public bool AppliesTo(WizardSelection selection) => selection.Workload.SelectedLamaCppWheelId.HasValue;

    public async Task InitializeAsync(WizardSelection selection, CancellationToken ct = default)
    {
        // Every field reset up front: the registry hands out long-lived module instances, so a
        // value left over from a previous workload would otherwise be contributed to this one.
        _wheelId = selection.Workload.SelectedLamaCppWheelId;
        WheelName = null;
        WheelUrl = null;

        if (_wheelId is not { } id) return;

        var wheels = await workloads.GetLamaCppWheelsAsync(ct).ConfigureAwait(false);
        var wheel = wheels.FirstOrDefault(w => w.Id == id);
        if (wheel is null) return;

        WheelName = wheel.Name;
        WheelUrl = wheel.Url;
    }

    public void Contribute(InstallationOptionsDraft draft)
    {
        draft.ResolvedLlamaCppWheelUrl = WheelUrl;
        draft.ResolvedLlamaCppWheelName = WheelName;
    }

    /// <summary>
    /// Stops the run at the wizard rather than at the step. The pipeline would schedule the step
    /// regardless and fail three quarters of the way through a long install.
    /// </summary>
    public ModuleValidation Validate() =>
        _wheelId is not null && WheelUrl is null
            ? ModuleValidation.Error(
                $"This workload requires the llama.cpp wheel {_wheelId}, which is not in the catalog.")
            : ModuleValidation.Ok();
}
