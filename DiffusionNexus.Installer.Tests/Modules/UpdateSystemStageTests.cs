using Bunit;
using DiffusionNexus.Installer.Core.Catalog;
using DiffusionNexus.Installer.Core.Content;
using DiffusionNexus.Installer.Core.Existing;
using DiffusionNexus.Installer.Core.Modules;
using DiffusionNexus.Installer.Core.Wizard;
using DiffusionNexus.Installer.Electron.Components.Wizard;
using DiffusionNexus.Installer.SDK.Models.Compatibility;
using DiffusionNexus.Installer.SDK.Models.Configuration;
using DiffusionNexus.Installer.SDK.Models.Entities;
using DiffusionNexus.Installer.SDK.Services.Hardware;
using DiffusionNexus.Installer.SDK.Services.Installation.Existing;
using FluentAssertions;
using Moq;
using Xunit;

namespace DiffusionNexus.Installer.Tests.Modules;

/// <summary>The update path's System stage: it warns, and never replaces a working setup.</summary>
public class UpdateSystemStageTests : BunitContext
{
    private const string Repo = @"E:\ComfyUI";

    private static InstallationConfiguration Workload(bool accelerators = true) => new()
    {
        Name = "A",
        Repository = new MainRepositorySettings { Type = RepositoryType.ComfyUI },
        Torch = new TorchSettings { TorchVersion = "2.11.0", CudaVersion = "13.0" },
        Python = new PythonEnvironmentSettings { PythonVersion = "3.12", InstallTriton = accelerators, InstallSageAttention = accelerators },
    };

    private static ExistingComfyUIProfile Profile() =>
        new(Repo, ExistingInstallSupport.Supported, Repo, Repo + @"\venv", "3.12.7", new InstalledTorchStack("2.8.0", "12.8", false, false), ManagerInstalled: true);

    private static IGpuDetectionService Gpu(GpuDetectionState state)
    {
        var gpu = new Mock<IGpuDetectionService>();
        gpu.Setup(g => g.DetectAsync(It.IsAny<CancellationToken>())).ReturnsAsync(new GpuDetectionResult(state, null));
        return gpu.Object;
    }

    private static IVcRuntimeDetectionService RuntimeMissing()
    {
        var runtime = new Mock<IVcRuntimeDetectionService>();
        runtime.Setup(r => r.Detect()).Returns(new VcRuntimeDetectionResult(VcRuntimeState.Missing, null));
        return runtime.Object;
    }

    private static WizardSelection Selection(bool updateOnly = false, bool accelerators = true)
    {
        var packs = updateOnly ? new List<InstallationConfiguration>() : [Workload(accelerators)];
        var union = ExtendConfigurationMerger.Union(packs.Select(p => new WorkloadChoice(p, 0)).ToList(), "Name");
        var selection = new WizardSelection { Workload = union.Configuration, Mode = WizardMode.UpdateExisting, UpdateOnly = updateOnly };
        selection.Existing = new ExistingRun(selection, union, packs, null);
        return selection;
    }

    private static Task<WizardPlan> PlanAsync(WizardSelection selection, GpuDetectionState gpu) =>
        new WizardModuleRegistry(() =>
        [
            new ComfyUpdateModule(),
            new ExistingGpuModule(Gpu(gpu)),
            new VcRuntimeModule(RuntimeMissing()),
            new GpuPreflightModule(Gpu(gpu)),
            new ShortcutsModule(),
            new DisclaimerModule(),
        ]).BuildPlanAsync(selection);

    private static DiskSpaceEstimate Estimate() => new(1, 2, true, []);

    [Fact]
    public async Task No_usable_gpu_warns_and_lets_the_user_go_on()
    {
        var selection = Selection();
        var plan = await PlanAsync(selection, GpuDetectionState.NoNvidiaGpu);
        selection.Existing!.Profile = Profile();
        plan.RefreshStages();

        var module = plan.Modules(WizardStage.System).OfType<ExistingGpuModule>().Single();

        module.Validate().IsValid.Should().BeTrue();
        selection.Existing.GpuUsable.Should().BeFalse();
        selection.Existing.UpgradeTorch.Should().BeFalse();
    }

    [Fact]
    public async Task No_usable_gpu_never_asks_for_a_cpu_install()
    {
        var plan = await PlanAsync(Selection(), GpuDetectionState.NoNvidiaGpu);

        plan.AllModules.OfType<GpuPreflightModule>().Should().BeEmpty();
        var options = plan.ToOptions();
        options.CpuTorch.Should().BeFalse();
    }

    [Fact]
    public async Task The_warning_says_what_stays()
    {
        var plan = await PlanAsync(Selection(), GpuDetectionState.NvidiaGpuWithoutDriver);
        var module = plan.AllModules.OfType<ExistingGpuModule>().Single();

        var cut = Render<ExistingGpuPanel>(p => p.Add(x => x.Module, module));

        cut.Find("[data-role=no-gpu]").TextContent.Should()
            .Be("No usable NVIDIA GPU was found. Torch and your launcher stay as they are, and this workload may not run on this machine.");
    }

    [Fact]
    public async Task Update_only_has_no_gpu_warning()
    {
        var plan = await PlanAsync(Selection(updateOnly: true), GpuDetectionState.NoNvidiaGpu);

        plan.Stages.Should().NotContain(WizardStage.System);
    }

    [Fact]
    public async Task An_unsure_probe_counts_as_usable()
    {
        var selection = Selection();
        var plan = await PlanAsync(selection, GpuDetectionState.Unknown);

        selection.Existing!.GpuUsable.Should().BeTrue();
        plan.AllModules.OfType<ExistingGpuModule>().Should().BeEmpty();
    }

    [Fact]
    public async Task A_driver_installed_during_a_side_trip_is_seen()
    {
        var selection = Selection();
        var state = GpuDetectionState.NoNvidiaGpu;
        var gpu = new Mock<IGpuDetectionService>();
        gpu.Setup(g => g.DetectAsync(It.IsAny<CancellationToken>())).ReturnsAsync(() => new GpuDetectionResult(state, null));
        var module = new ExistingGpuModule(gpu.Object);
        await module.InitializeAsync(selection);

        state = GpuDetectionState.CudaCapable;
        await module.RefreshAfterResumeAsync();

        selection.Existing!.GpuUsable.Should().BeTrue();
    }

    [Fact]
    public async Task The_runtime_step_shows_only_when_torch_will_change_and_follows_the_checkbox()
    {
        var selection = Selection();
        var plan = await PlanAsync(selection, GpuDetectionState.CudaCapable);
        selection.Existing!.Profile = Profile();
        plan.RefreshStages();

        plan.Modules(WizardStage.System).OfType<VcRuntimeModule>().Should().ContainSingle();

        selection.Existing.RunUpdate = false;
        plan.RefreshStages();

        plan.Stages.Should().NotContain(WizardStage.System);

        selection.Existing.RunUpdate = true;
        plan.RefreshStages();

        plan.Modules(WizardStage.System).OfType<VcRuntimeModule>().Should().ContainSingle();
    }

    [Fact]
    public async Task No_accelerators_means_no_runtime_step()
    {
        var selection = Selection(accelerators: false);
        var plan = await PlanAsync(selection, GpuDetectionState.CudaCapable);
        selection.Existing!.Profile = Profile();
        plan.RefreshStages();

        plan.Stages.Should().NotContain(WizardStage.System);
    }

    [Fact]
    public async Task There_are_no_shortcuts_on_this_path()
    {
        var plan = await PlanAsync(Selection(), GpuDetectionState.CudaCapable);

        plan.AllModules.OfType<ShortcutsModule>().Should().BeEmpty();
    }

    [Fact]
    public void Llama_is_offered_on_this_path() =>
        new LlamaCppModule(Mock.Of<IWorkloadSource>()).UsedIn(WizardMode.UpdateExisting).Should().BeTrue();

    [Fact]
    public async Task The_estimate_counts_only_what_this_path_writes()
    {
        var workload = Workload();
        workload.ModelDownloads.Add(new ModelDownload { Name = "m", Url = "https://h/m.safetensors" });
        var union = ExtendConfigurationMerger.Union([new WorkloadChoice(workload, 0)]);
        var selection = new WizardSelection { Workload = union.Configuration, Mode = WizardMode.UpdateExisting };
        selection.Existing = new ExistingRun(selection, union, [workload], null);
        DiskSpaceRequest? seen = null;
        var estimator = new Mock<IDiskSpaceEstimator>();
        estimator.Setup(e => e.EstimateAsync(It.IsAny<DiskSpaceRequest>(), It.IsAny<CancellationToken>()))
            .Callback<DiskSpaceRequest, CancellationToken>((r, _) => seen = r)
            .ReturnsAsync(Estimate());
        var scanner = new Mock<IModelPresenceScanner>();
        scanner.Setup(s => s.Scan(It.IsAny<ModelScanRequest>())).Returns([]);
        var module = new ModelSelectionModule(scanner.Object, estimator.Object) { ScanWait = TimeSpan.FromSeconds(30) };
        await module.InitializeAsync(selection);
        selection.Existing.Profile = Profile();

        selection.Existing.RunUpdate = false;

        await module.RefreshEstimateAsync();

        seen!.ModelsOnly.Should().BeTrue("no repository or venv gets created here");
    }

    [Fact]
    public async Task The_estimate_makes_room_for_torch_when_it_changes()
    {
        var workload = Workload();
        workload.ModelDownloads.Add(new ModelDownload { Name = "m", Url = "https://h/m.safetensors" });
        var union = ExtendConfigurationMerger.Union([new WorkloadChoice(workload, 0)]);
        var selection = new WizardSelection { Workload = union.Configuration, Mode = WizardMode.UpdateExisting };
        selection.Existing = new ExistingRun(selection, union, [workload], null);
        DiskSpaceRequest? seen = null;
        var estimator = new Mock<IDiskSpaceEstimator>();
        estimator.Setup(e => e.EstimateAsync(It.IsAny<DiskSpaceRequest>(), It.IsAny<CancellationToken>()))
            .Callback<DiskSpaceRequest, CancellationToken>((r, _) => seen = r)
            .ReturnsAsync(Estimate());
        var scanner = new Mock<IModelPresenceScanner>();
        scanner.Setup(s => s.Scan(It.IsAny<ModelScanRequest>())).Returns([]);
        var module = new ModelSelectionModule(scanner.Object, estimator.Object) { ScanWait = TimeSpan.FromSeconds(30) };
        await module.InitializeAsync(selection);
        selection.Existing.Profile = Profile();

        await module.RefreshEstimateAsync();

        seen!.ModelsOnly.Should().BeFalse("a new torch takes gigabytes of its own");
    }
}
