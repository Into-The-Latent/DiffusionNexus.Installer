using DiffusionNexus.Installer.Core.Install;
using DiffusionNexus.Installer.Core.Wizard;
using DiffusionNexus.Installer.SDK.Models.Configuration;
using DiffusionNexus.Installer.SDK.Services;
using FluentAssertions;
using Xunit;

namespace DiffusionNexus.Installer.Tests.Install;

/// <summary>
/// What the "Open folder" and "Start app &amp; close" buttons act on. The rules mirror the 1.x
/// wizard: the folder is the one the user picked, the launcher is resolved from the path the
/// pipeline actually cloned into — which is not the same directory (the SDK appends the repo name,
/// and AI Toolkit's launcher sits one level above it).
/// </summary>
public class PostInstallTargetsTests
{
    private static async Task<WizardPlan> PlanAsync(RepositoryType type, string folder)
    {
        var workload = new InstallationConfiguration { Name = "Test" };
        workload.Repository.Type = type;

        var plan = await new WizardModuleRegistry(() => [])
            .BuildPlanAsync(new WizardSelection { Workload = workload });
        plan.Selection.TargetFolder = folder;
        return plan;
    }

    [Fact]
    public async Task The_folder_is_the_one_the_user_picked_not_the_repository_path()
    {
        var plan = await PlanAsync(RepositoryType.ComfyUI, @"C:\Installs\Comfy");
        var result = InstallationResult.Success("done", @"C:\Installs\Comfy\ComfyUI");

        PostInstallTargets.From(plan, result).Folder.Should().Be(@"C:\Installs\Comfy");
    }

    [Fact]
    public async Task The_launcher_is_resolved_from_the_repository_path()
    {
        var repoPath = Path.Combine(Path.GetTempPath(), "dn-launcher-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(repoPath);
        var launcher = Path.Combine(repoPath, OperatingSystem.IsWindows() ? "run_nvidia.bat" : "run_nvidia.sh");
        File.WriteAllText(launcher, string.Empty);

        try
        {
            var targets = PostInstallTargets.From(await PlanAsync(RepositoryType.ComfyUI, repoPath),
                InstallationResult.Success("done", repoPath));

            targets.LauncherScript.Should().Be(launcher);
        }
        finally
        {
            Directory.Delete(repoPath, recursive: true);
        }
    }

    [Fact]
    public async Task Without_a_repository_path_there_is_nothing_to_launch()
    {
        // A run that failed before the clone has no repository. Offering to start it would run
        // whatever the fallback resolved to — at best nothing, at worst a stale earlier install.
        var targets = PostInstallTargets.From(await PlanAsync(RepositoryType.ComfyUI, @"C:\Installs\Comfy"),
            InstallationResult.Failure("cloning failed"));

        targets.LauncherScript.Should().BeNull();
        targets.Folder.Should().Be(@"C:\Installs\Comfy");
    }

    [Fact]
    public async Task Without_a_result_there_are_no_targets_to_act_on()
    {
        var targets = PostInstallTargets.From(await PlanAsync(RepositoryType.ComfyUI, @"C:\Installs\Comfy"), result: null);

        targets.LauncherScript.Should().BeNull();
        targets.Folder.Should().Be(@"C:\Installs\Comfy");
    }
}
