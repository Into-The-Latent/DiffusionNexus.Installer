using DiffusionNexus.Installer.SDK.Services;
using DiffusionNexus.Installer.Core.Catalog;
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

    private static IWorkloadSource Wheels(params LamaCppWheel[] wheels)
    {
        var source = new Mock<IWorkloadSource>();
        source.Setup(s => s.GetLamaCppWheelsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(wheels);
        return source.Object;
    }

    private static IVcRuntimeDetectionService VcRuntime(
        VcRuntimeState state = VcRuntimeState.Present)
    {
        var vc = new Mock<IVcRuntimeDetectionService>();
        vc.Setup(v => v.Detect()).Returns(new VcRuntimeDetectionResult(state));
        return vc.Object;
    }

    private static WizardModuleRegistry Registry(params LamaCppWheel[] wheels) => new(
    [
        new InstallFolderModule(Settings(), new PreInstallationService()),
        new ComfyFoldersModule(Settings()),
        new GpuPreflightModule(Gpu()),
        new VcRuntimeModule(VcRuntime()),
        new LlamaCppModule(Wheels(wheels)),
        new ShortcutsModule(),
        new DisclaimerModule(),
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

        plan.AllModules.Select(m => m.Id)
            .Should().BeEquivalentTo("install-folder", "shortcuts", "disclaimer");
    }

    [Fact]
    public async Task Blank_comfyui_adds_only_the_folders_module()
    {
        var plan = await Registry().BuildPlanAsync(
            new WizardSelection { Workload = Workload(RepositoryType.ComfyUI) });

        plan.AllModules.Select(m => m.Id)
            .Should().BeEquivalentTo("install-folder", "comfy-folders", "shortcuts", "disclaimer");
    }

    [Fact]
    public void A_workload_declaring_custom_nodes_is_still_installable()
    {
        // Blank ComfyUI ships ComfyUI-Manager. With no CustomNodes module the pipeline clones every
        // declared repo, which is exactly right -- the user just cannot deselect any.
        var blankComfy = Workload(RepositoryType.ComfyUI);
        blankComfy.GitRepositories.Add(new GitRepository());

        Registry().IsInstallable(blankComfy).Should().BeTrue();
    }

    [Fact]
    public void A_workload_declaring_accelerators_is_still_installable()
    {
        // AI-Toolkit sets installTriton. The Triton step runs off the workload's own flag, not off
        // an option, so no module is needed for a correct install.
        var toolkit = Workload(RepositoryType.AIToolkit);
        toolkit.Python.InstallTriton = true;

        Registry().IsInstallable(toolkit).Should().BeTrue();
    }

    [Fact]
    public void A_workload_needing_a_vram_tier_is_not_installable()
    {
        var pack = Workload(RepositoryType.ComfyUI);
        pack.Vram.VramProfiles = "8,12,16,24,32";

        Registry().IsInstallable(pack).Should().BeFalse();
    }

    [Fact]
    public void A_workload_needing_LlamaCpp_is_installable_because_a_module_resolves_the_wheel()
    {
        var workload = Workload(RepositoryType.ComfyUI);
        workload.SelectedLamaCppWheelId = Guid.NewGuid();

        Registry().IsInstallable(workload).Should().BeTrue();
    }

    [Fact]
    public async Task The_LlamaCpp_module_contributes_the_resolved_wheel_url()
    {
        // The whole point of the module: without ResolvedLlamaCppWheelUrl the step fails, and the
        // pipeline schedules it off the workload's wheel id whatever the wizard does.
        var wheelId = Guid.NewGuid();
        var workload = Workload(RepositoryType.ComfyUI);
        workload.SelectedLamaCppWheelId = wheelId;

        var wheel = new LamaCppWheel
        {
            Id = wheelId,
            Name = "llama_cpp_python-cu128",
            Url = "https://example.invalid/llama.whl",
        };

        var plan = await Registry(wheel).BuildPlanAsync(new WizardSelection { Workload = workload });
        var options = plan.ToOptions();

        options.ResolvedLlamaCppWheelUrl.Should().Be("https://example.invalid/llama.whl");
        options.ResolvedLlamaCppWheelName.Should().Be("llama_cpp_python-cu128");
    }

    [Fact]
    public async Task A_wheel_id_the_catalog_does_not_have_fails_validation_rather_than_the_install()
    {
        var workload = Workload(RepositoryType.ComfyUI);
        workload.SelectedLamaCppWheelId = Guid.NewGuid();

        // Registry() with no wheels: the id resolves to nothing.
        var plan = await Registry().BuildPlanAsync(new WizardSelection { Workload = workload });

        // Not ContainSingle: the unaccepted disclaimer is a second, expected failure here.
        plan.Validate().Select(v => v.ErrorMessage)
            .Should().ContainSingle(m => m!.Contains("not in the catalog"));
    }

    [Fact]
    public void A_workload_the_pipeline_would_refuse_outright_is_not_installable()
    {
        // Torch 2.8.0 has no CUDA 13.0 wheel, so InstallationPipeline aborts before step 1 and
        // stamps every planned step NotRun. Offering the card is offering a guaranteed failure.
        var workload = Workload(RepositoryType.ComfyUI);
        workload.Torch.TorchVersion = "2.8.0";
        workload.Torch.CudaVersion = "13.0";

        Registry().IsInstallable(workload).Should().BeFalse();
    }

    [Fact]
    public void The_disclaimer_blocks_Next_until_it_is_accepted()
    {
        // Confirm has no modules of its own, so without this one ValidationErrors is always empty
        // there and Next is unconditionally enabled in front of an irreversible install.
        var module = new DisclaimerModule();

        module.Validate().IsValid.Should().BeFalse();

        module.Accepted = true;
        module.Validate().IsValid.Should().BeTrue();
    }
}
