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
    public static string Resolve(InstallationConfiguration workload, string targetFolder)
    {
        ArgumentNullException.ThrowIfNull(workload);
        ArgumentException.ThrowIfNullOrWhiteSpace(targetFolder);

        var url = workload.Repository.RepositoryUrl;
        var normalizedTarget = PathNormalizer.NormalizeTargetDirectory(
            targetFolder,
            url,
            workload.Repository.Type == RepositoryType.AIToolkit ? "AI-Toolkit" : null);

        return Path.Combine(normalizedTarget, PathNormalizer.GetRepositoryName(url));
    }
}
