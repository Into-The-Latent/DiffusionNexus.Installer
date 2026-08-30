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
    /// variant at no tier, which is a wrong install, not an unrefined one.
    /// </para>
    /// </summary>
    public const WorkloadCapability Blocking =
        WorkloadCapability.VramProfile | WorkloadCapability.ModelDownloads;

    /// <summary>Pure function of the workload. No module involvement — see WorkloadCapability.</summary>
    public static WorkloadCapability Detect(InstallationConfiguration workload)
    {
        ArgumentNullException.ThrowIfNull(workload);

        var caps = WorkloadCapability.None;

        // ComfyUI gets a model base folder AND an output folder; AI-Toolkit only writes
        // extra_model_paths.yaml, so it gets the model-folder half of the same module.
        if (workload.Repository.Type is RepositoryType.ComfyUI or RepositoryType.AIToolkit)
            caps |= WorkloadCapability.ComfyFolders;

        if (!string.IsNullOrWhiteSpace(workload.Vram.VramProfiles))
            caps |= WorkloadCapability.VramProfile;

        if (workload.ModelDownloads.Count > 0)
            caps |= WorkloadCapability.ModelDownloads;

        if (workload.GitRepositories.Count > 0)
            caps |= WorkloadCapability.CustomNodes;

        if (workload.Workflows.Count > 0)
            caps |= WorkloadCapability.Workflows;

        if (workload.Python.InstallTriton || workload.Python.InstallSageAttention)
            caps |= WorkloadCapability.Accelerators;

        return caps;
    }

    /// <summary>The subset of <see cref="Detect"/> that actually gates installability.</summary>
    public static WorkloadCapability DetectBlocking(InstallationConfiguration workload) =>
        Detect(workload) & Blocking;
}
