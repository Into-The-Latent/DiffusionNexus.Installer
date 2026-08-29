using DiffusionNexus.Installer.Core.Modules;
using DiffusionNexus.Installer.Core.Wizard;
using DiffusionNexus.Installer.SDK.Models.Configuration;
using DiffusionNexus.Installer.SDK.Models.Entities;
using DiffusionNexus.Installer.SDK.Models.Installation;
using DiffusionNexus.Installer.SDK.Services.Hardware;
using DiffusionNexus.Installer.SDK.Services.Settings;
using FluentAssertions;
using Moq;
using Xunit;

namespace DiffusionNexus.Installer.Tests.Wizard;

/// <summary>
/// Detect() decides whether a workload is offered; AppliesTo() decides whether a module renders.
/// If they disagree, a workload is either offered without the panel it needs, or shows a panel the
/// gate never accounted for. These tests pin them together.
/// </summary>
public class CapabilityAgreementTests
{
    private static IUserSettingsRepository Settings()
    {
        var repo = new Mock<IUserSettingsRepository>();
        repo.Setup(r => r.GetOrCreateForCurrentUserAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new UserSettings());
        return repo.Object;
    }

    private static IGpuDetectionService Gpu(GpuDetectionState state = GpuDetectionState.CudaCapable)
    {
        var gpu = new Mock<IGpuDetectionService>();
        gpu.Setup(g => g.DetectAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new GpuDetectionResult(state));
        return gpu.Object;
    }

    private static WizardModuleRegistry Registry() => new(
    [
        new InstallFolderModule(Settings()),
        new ComfyFoldersModule(Settings()),
        new GpuPreflightModule(Gpu()),
        new ShortcutsModule(),
    ]);

    private static InstallationConfiguration Workload(RepositoryType type)
    {
        var w = new InstallationConfiguration { Name = type.ToString() };
        w.Repository.Type = type;
        return w;
    }

    [Theory]
    [InlineData(RepositoryType.A1111)]
    [InlineData(RepositoryType.Forge)]
    [InlineData(RepositoryType.Fooocus)]
    [InlineData(RepositoryType.AceStep)]
    [InlineData(RepositoryType.AIToolkit)]
    [InlineData(RepositoryType.ComfyUI)]
    public void Every_slice_one_workload_is_installable(RepositoryType type)
        => Registry().IsInstallable(Workload(type)).Should().BeTrue();

    [Fact]
    public void A_content_heavy_comfyui_pack_is_not_installable_in_slice_one()
    {
        var pack = Workload(RepositoryType.ComfyUI);
        pack.Vram.VramProfiles = "8,12,16,24,32";
        pack.ModelDownloads.Add(new ModelDownload());

        Registry().IsInstallable(pack).Should().BeFalse();
    }

    [Theory]
    [InlineData(RepositoryType.ComfyUI, true)]
    [InlineData(RepositoryType.AIToolkit, true)]
    [InlineData(RepositoryType.Fooocus, false)]
    public async Task Detect_and_AppliesTo_agree_on_the_comfy_folders_capability(
        RepositoryType type, bool expected)
    {
        var workload = Workload(type);
        var selection = new WizardSelection { Workload = workload };

        var detected = WorkloadCapabilities.Detect(workload).HasFlag(WorkloadCapability.ComfyFolders);

        var plan = await Registry().BuildPlanAsync(selection);
        var rendered = plan.AllModules.Any(m => m.Satisfies == WorkloadCapability.ComfyFolders);

        detected.Should().Be(expected);
        rendered.Should().Be(detected);
    }

    [Fact]
    public async Task A_thin_workload_shows_exactly_the_unconditional_modules()
    {
        var plan = await Registry().BuildPlanAsync(
            new WizardSelection { Workload = Workload(RepositoryType.Fooocus) });

        plan.AllModules.Select(m => m.Id).Should().BeEquivalentTo("install-folder", "shortcuts");
    }

    [Fact]
    public async Task Blank_comfyui_adds_only_the_folders_module()
    {
        var plan = await Registry().BuildPlanAsync(
            new WizardSelection { Workload = Workload(RepositoryType.ComfyUI) });

        plan.AllModules.Select(m => m.Id)
            .Should().BeEquivalentTo("install-folder", "comfy-folders", "shortcuts");
    }
}
