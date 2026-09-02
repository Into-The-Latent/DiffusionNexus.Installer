using DiffusionNexus.Installer.SDK.Models.Compatibility;
using DiffusionNexus.Installer.SDK.Models.Configuration;

namespace DiffusionNexus.Installer.Core.Wizard;

public static class WorkloadCapabilities
{
    /// <summary>
    /// The capabilities whose absence makes an install WRONG, rather than merely un-narrowable.
    /// <para>
    /// A module only ever narrows what the catalog declares or configures it, so for most
    /// capabilities having no module simply means the catalog's own declaration stands: with no
    /// module, every declared custom node is cloned, every declared workflow is exported, and the
    /// accelerator steps run off the workload's own flags. Those are correct defaults.
    /// VRAM and model selection are different: without them a tiered pack downloads every tier's
    /// variant at no tier, which is a wrong install, not an unrefined one. LlamaCpp is the same
    /// shape of trap: the pipeline schedules the step and then fails in the handler on a null
    /// wheel URL unless something resolves the wheel first.
    /// </para>
    /// </summary>
    public const WorkloadCapability Blocking =
        WorkloadCapability.VramProfile | WorkloadCapability.ModelDownloads | WorkloadCapability.LlamaCpp;

    /// <summary>Pure function of the workload. No module involvement — see WorkloadCapability.</summary>
    public static WorkloadCapability Detect(InstallationConfiguration workload)
    {
        ArgumentNullException.ThrowIfNull(workload);

        var caps = WorkloadCapability.None;

        // ComfyUI gets a model base folder AND an output folder; AI-Toolkit only writes
        // extra_model_paths.yaml, so it gets the model-folder half of the same module.
        if (workload.Repository.Type is RepositoryType.ComfyUI or RepositoryType.AIToolkit)
            caps |= WorkloadCapability.ComfyFolders;

        // The same parser VramProfileModule.AppliesTo uses. A non-blank but unparseable string
        // must not be gated as "needs a tier" -- the module would decline to render one and the
        // card could never be installed.
        if (VramTiers.Parse(workload.Vram.VramProfiles).Count > 0)
            caps |= WorkloadCapability.VramProfile;

        if (workload.ModelDownloads.Count > 0)
            caps |= WorkloadCapability.ModelDownloads;

        if (workload.GitRepositories.Count > 0)
            caps |= WorkloadCapability.CustomNodes;

        if (workload.Workflows.Count > 0)
            caps |= WorkloadCapability.Workflows;

        if (workload.Python.InstallTriton || workload.Python.InstallSageAttention)
            caps |= WorkloadCapability.Accelerators;

        // SelectedLamaCppWheelId, NOT InstallLamaCpp: the wheel id is the field the SDK actually
        // schedules on -- ComfyUIInstallationFlow adds InstallationStep.InstallLlamaCpp when
        // SelectedLamaCppWheelId.HasValue, and LlamaCppInstallStepHandler.ShouldExecute reads the
        // same field. InstallLamaCpp is inert at install time, so keying the gate on it detected
        // nothing on the one shipped workload that needs the step and blocked nothing on any other.
        if (workload.SelectedLamaCppWheelId.HasValue)
            caps |= WorkloadCapability.LlamaCpp;

        return caps;
    }

    /// <summary>The subset of <see cref="Detect"/> that actually gates installability.</summary>
    public static WorkloadCapability DetectBlocking(InstallationConfiguration workload) =>
        Detect(workload) & Blocking;

    /// <summary>
    /// Why the pipeline would refuse this workload before running a single step, or null when it
    /// would not. No module can fix this — it is a property of the catalog entry's own torch/CUDA
    /// pairing — so it is a separate question from capability coverage.
    /// <para>
    /// InstallationPipeline.TryValidateTorchCompatibility runs exactly this check and returns
    /// Failure with every planned step stamped NotRun. Offering such a workload means the user
    /// fills in the whole wizard, clicks Install, and gets a wall of "Not run" rows. Mirroring the
    /// pipeline's own early-outs keeps the two answers identical.
    /// </para>
    /// </summary>
    public static string? DetectIncompatibility(InstallationConfiguration workload)
    {
        ArgumentNullException.ThrowIfNull(workload);

        // Only ComfyUI authors its own torch settings; every other workload is pinned by
        // TorchSettingsPolicy to a pairing the catalog cannot get wrong.
        if (!TorchSettingsPolicy.AuthorsTorchSettings(workload.Repository.Type))
            return null;

        var check = TorchCompatibilityCatalog.Check(workload.GetEffectiveTorch(), workload.Python);
        return check.IsCompatible
            ? null
            : string.Join(" ", check.Errors.Select(e => e.Message));
    }
}
