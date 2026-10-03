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

    private static GpuPreflightModule Module(params GpuDetectionResult[] detections)
    {
        var gpu = new Mock<IGpuDetectionService>();
        var sequence = gpu.SetupSequence(g => g.DetectAsync(It.IsAny<CancellationToken>()));
        foreach (var d in detections) sequence = sequence.ReturnsAsync(d);
        return new GpuPreflightModule(gpu.Object);
    }

    [Fact]
    public async Task A_gpu_made_usable_during_a_side_trip_is_seen_on_resume()
    {
        // #45: the System stage said "no usable GPU", the user installed the driver, read Licences,
        // came Back. A consent to the CPU build given for a machine without a GPU must not put the
        // CPU wheel on a machine that now has one; the stage must not block on it either.
        var module = Module(new GpuDetectionResult(GpuDetectionState.NvidiaGpuWithoutDriver),
                            new GpuDetectionResult(GpuDetectionState.CudaCapable, "RTX 4090"));
        var selection = Selection(RepositoryType.ComfyUI);
        await module.InitializeAsync(selection);
        module.AcceptCpuOnly = true;

        await module.RefreshAfterResumeAsync();

        module.GpuFound.Should().BeTrue();
        module.GpuName.Should().Be("RTX 4090");
        module.AcceptCpuOnly.Should().BeFalse("the consent was about a machine without a GPU");
        module.Validate().IsValid.Should().BeTrue();
        var draft = new InstallationOptionsDraft();
        module.Contribute(draft);
        draft.CpuTorch.Should().BeFalse();
    }

    [Fact]
    public async Task An_inconclusive_probe_on_resume_keeps_the_earlier_answer()
    {
        var module = Module(new GpuDetectionResult(GpuDetectionState.NoNvidiaGpu),
                            new GpuDetectionResult(GpuDetectionState.Unknown));
        var selection = Selection(RepositoryType.ComfyUI);
        await module.InitializeAsync(selection);
        module.AcceptCpuOnly = true;

        await module.RefreshAfterResumeAsync();

        module.GpuFound.Should().BeFalse();
        module.AcceptCpuOnly.Should().BeTrue("nothing new was learned, so nothing the user said is dropped");
        var draft = new InstallationOptionsDraft();
        module.Contribute(draft);
        draft.CpuTorch.Should().BeTrue();
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
