using DiffusionNexus.Installer.Core.Wizard;
using DiffusionNexus.Installer.SDK.Services;

namespace DiffusionNexus.Installer.Core.Install;

/// <summary>
/// What the end-of-install buttons act on: the folder to show, and the script that starts what was
/// installed. Resolved here rather than in the Install screen because the two are NOT the same
/// path and the difference is not obvious — the pipeline clones into a subdirectory of the chosen
/// folder, and several workloads keep their launcher somewhere else again (AI Toolkit one level
/// above the repository, a portable ComfyUI in the package root).
/// </summary>
/// <param name="Folder">The folder the user chose, or null when no folder was chosen.</param>
/// <param name="LauncherScript">
/// The script that starts the installed app, or null when there is nothing to start — a run that
/// failed before the clone has no repository, and offering to launch it would run whatever the
/// helper's fallback resolved to, at worst a stale earlier install.
/// </param>
public sealed record PostInstallTargets(string? Folder, string? LauncherScript)
{
    public static PostInstallTargets From(WizardPlan plan, InstallationResult? result)
    {
        ArgumentNullException.ThrowIfNull(plan);

        var folder = string.IsNullOrWhiteSpace(plan.Selection.TargetFolder)
            ? null
            : plan.Selection.TargetFolder;

        var repositoryPath = result?.RepositoryPath;
        var launcher = string.IsNullOrWhiteSpace(repositoryPath)
            ? null
            : PostInstallLaunchHelper.DetermineLauncherScriptPath(
                repositoryPath, plan.Selection.Workload.Repository.Type);

        return new PostInstallTargets(folder, launcher);
    }
}
