// DiffusionNexus.Installer.Core/Content/RepositoryPaths.cs
using DiffusionNexus.Installer.SDK.Models.Configuration;
using DiffusionNexus.Installer.SDK.Services.Installation.Utilities;

namespace DiffusionNexus.Installer.Core.Content;

/// <summary>
/// Where the main repository will land for an install folder — derived exactly the way
/// InstallationOrchestrator and CloneMainRepositoryStepHandler (NormalizeTargetDirectory, each
/// once) derive it, so a pre-install scan looks in the folder the pipeline will actually write to.
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
    /// <para>
    /// Twice, because SDK 2.1.0 does it twice: InstallationOrchestrator normalizes the target and
    /// CloneMainRepositoryStepHandler normalizes the result again, so "E:\AI\ComfyUI\ComfyUI"
    /// installs into E:\AI\ComfyUI too.
    /// </para>
    /// </summary>
    public static string NormalizedTarget(InstallationConfiguration workload, string targetFolder)
    {
        ArgumentNullException.ThrowIfNull(workload);
        ArgumentException.ThrowIfNullOrWhiteSpace(targetFolder);

        var url = workload.Repository.RepositoryUrl;
        var folderName = workload.Repository.Type == RepositoryType.AIToolkit ? "AI-Toolkit" : null;
        var once = PathNormalizer.NormalizeTargetDirectory(targetFolder, url, folderName);
        return PathNormalizer.NormalizeTargetDirectory(once, url, folderName);
    }
}
