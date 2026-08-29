using DiffusionNexus.Installer.Core.Wizard;
using DiffusionNexus.Installer.SDK.Models.Configuration;
using DiffusionNexus.Installer.SDK.Services.Hardware;

namespace DiffusionNexus.Installer.Core.Modules;

/// <summary>
/// Warns before an install that cannot work. ComfyUI can fall back to a CPU-only torch build;
/// every other workload has to stop.
/// <para>
/// Detection is inconclusive on plenty of real machines, so <see cref="GpuDetectionState.Unknown"/>
/// fails open — an unsure probe must never block an install that would have worked.
/// </para>
/// </summary>
public sealed class GpuPreflightModule(IGpuDetectionService gpuDetection) : IWizardModule
{
    private GpuDetectionState _state = GpuDetectionState.Unknown;

    public string Id => "gpu-preflight";
    public WizardStage Stage => WizardStage.System;
    public int Order => 0;
    public WorkloadCapability Satisfies => WorkloadCapability.None;

    /// <summary>Name of the detected adapter, when the probe found one.</summary>
    public string? GpuName { get; private set; }

    /// <summary>True only for ComfyUI, which ships a CPU launcher and a CPU wheel.</summary>
    public bool CanOfferCpuFallback { get; private set; }

    /// <summary>Set when the user has seen the consequence and chosen to continue on CPU.</summary>
    public bool AcceptCpuOnly { get; set; }

    /// <summary>
    /// The probe result, not the selection, decides this — so it is computed once and read from
    /// both AppliesTo and Contribute rather than passing a fake selection around.
    /// </summary>
    private bool NoUsableGpu =>
        _state is GpuDetectionState.NoNvidiaGpu or GpuDetectionState.NvidiaGpuWithoutDriver;

    public bool AppliesTo(WizardSelection selection) => NoUsableGpu;

    public async Task InitializeAsync(WizardSelection selection, CancellationToken ct = default)
    {
        var result = await gpuDetection.DetectAsync(ct).ConfigureAwait(false);
        _state = result.State;
        GpuName = result.GpuName;
        CanOfferCpuFallback = selection.Workload.Repository.Type == RepositoryType.ComfyUI;
    }

    public void Contribute(InstallationOptionsDraft draft)
    {
        if (NoUsableGpu && CanOfferCpuFallback && AcceptCpuOnly)
            draft.CpuTorch = true;
    }

    public ModuleValidation Validate()
    {
        if (_state is GpuDetectionState.CudaCapable or GpuDetectionState.Unknown)
            return ModuleValidation.Ok();

        if (!CanOfferCpuFallback)
            return ModuleValidation.Error(
                "No compatible NVIDIA GPU was found. This workload requires one and cannot run on CPU.");

        return AcceptCpuOnly
            ? ModuleValidation.Ok()
            : ModuleValidation.Error("No compatible NVIDIA GPU was found. Accept the CPU-only install to continue.");
    }
}
