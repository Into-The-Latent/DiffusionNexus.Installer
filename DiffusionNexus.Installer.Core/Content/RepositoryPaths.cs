// DiffusionNexus.Installer.Core/Content/RepositoryPaths.cs
using DiffusionNexus.Installer.SDK.Models.Configuration;
using DiffusionNexus.Installer.SDK.Services.Installation.Utilities;

namespace DiffusionNexus.Installer.Core.Content;

/// <summary>
/// Where the main repository will land for an install folder — derived exactly the way
/// InstallationOrchestrator (NormalizeTargetDirectory) and InstallationContext.GetRepositoryPath
/// derive it, so a pre-install scan looks in the folder the pipeline will actually write to.
/// </summary>
public static class RepositoryPaths
{
    public static string Resolve(InstallationConfiguration workload, string targetFolder) =>
        Path.Combine(
            NormalizedTarget(workload, targetFolder),
            PathNormalizer.GetRepositoryName(workload.Repository.RepositoryUrl));

    /// <summary>
    /// The folder the repository folder is created in: the chosen folder, minus a last folder
    /// already named after the repository ("E:\AI\ComfyUI" means E:\AI). Anything that appends
    /// the repository name itself, like PreInstallationValidator, must be given this, not the
    /// chosen folder, or it looks at E:\AI\ComfyUI\ComfyUI while the install goes to E:\AI\ComfyUI.
    /// </summary>
    public static string NormalizedTarget(InstallationConfiguration workload, string targetFolder)
    {
        ArgumentNullException.ThrowIfNull(workload);
        ArgumentException.ThrowIfNullOrWhiteSpace(targetFolder);

        return PathNormalizer.NormalizeTargetDirectory(
            targetFolder,
            workload.Repository.RepositoryUrl,
            workload.Repository.Type == RepositoryType.AIToolkit ? "AI-Toolkit" : null);
    }
}
