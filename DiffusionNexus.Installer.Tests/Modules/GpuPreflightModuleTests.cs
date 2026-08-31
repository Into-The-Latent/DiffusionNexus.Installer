using DiffusionNexus.Installer.Core.Modules;
using DiffusionNexus.Installer.Core.Wizard;
using DiffusionNexus.Installer.SDK.Models.Configuration;
using DiffusionNexus.Installer.SDK.Services.Hardware;
using FluentAssertions;
using Moq;
using Xunit;

namespace DiffusionNexus.Installer.Tests.Modules;

public class GpuPreflightModuleTests
{
    private static WizardSelection Selection(RepositoryType type)
    {
        var w = new InstallationConfiguration();
        w.Repository.Type = type;
        return new WizardSelection { Workload = w };
    }

    private static GpuPreflightModule Module(GpuDetectionState state)
    {
        var gpu = new Mock<IGpuDetectionService>();
        gpu.Setup(g => g.DetectAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new GpuDetectionResult(state));
        return new GpuPreflightModule(gpu.Object);
    }

    [Fact]
    public async Task Does_not_apply_when_a_cuda_capable_gpu_is_present()
    {
        var module = Module(GpuDetectionState.CudaCapable);
        var selection = Selection(RepositoryType.ComfyUI);

        await module.InitializeAsync(selection);

        module.AppliesTo(selection).Should().BeFalse();
    }

    [Fact]
    public async Task Fails_open_on_an_inconclusive_probe()
    {
        var module = Module(GpuDetectionState.Unknown);
        var selection = Selection(RepositoryType.ComfyUI);

        await module.InitializeAsync(selection);

        module.AppliesTo(selection).Should().BeFalse();
    }

    [Fact]
    public async Task Applies_and_offers_cpu_fallback_for_comfyui_without_a_gpu()
    {
        var module = Module(GpuDetectionState.NoNvidiaGpu);
        var selection = Selection(RepositoryType.ComfyUI);

        await module.InitializeAsync(selection);

        module.AppliesTo(selection).Should().BeTrue();
        module.CanOfferCpuFallback.Should().BeTrue();
    }

    [Fact]
    public async Task Blocks_non_comfyui_workloads_without_a_gpu()
    {
        var module = Module(GpuDetectionState.NoNvidiaGpu);
        var selection = Selection(RepositoryType.Forge);

        await module.InitializeAsync(selection);

        module.CanOfferCpuFallback.Should().BeFalse();
        module.Validate().IsValid.Should().BeFalse();
    }

    [Fact]
    public async Task Cpu_fallback_is_only_valid_once_accepted()
    {
        var module = Module(GpuDetectionState.NoNvidiaGpu);
        await module.InitializeAsync(Selection(RepositoryType.ComfyUI));

        module.Validate().IsValid.Should().BeFalse();

        module.AcceptCpuOnly = true;

        module.Validate().IsValid.Should().BeTrue();
    }

    [Fact]
    public async Task Accepting_cpu_only_contributes_cpu_torch()
    {
        var module = Module(GpuDetectionState.NoNvidiaGpu);
        await module.InitializeAsync(Selection(RepositoryType.ComfyUI));
        module.AcceptCpuOnly = true;

        var draft = new InstallationOptionsDraft();
        module.Contribute(draft);

        draft.CpuTorch.Should().BeTrue();
    }

    [Fact]
    public async Task A_driverless_nvidia_card_is_treated_as_no_usable_gpu()
    {
        var module = Module(GpuDetectionState.NvidiaGpuWithoutDriver);
        var selection = Selection(RepositoryType.ComfyUI);

        await module.InitializeAsync(selection);

        module.AppliesTo(selection).Should().BeTrue();
    }
}
