using DiffusionNexus.Installer.SDK.Models.Configuration;

namespace DiffusionNexus.Installer.Core.Wizard;

public static class WorkloadCapabilities
{
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
}
