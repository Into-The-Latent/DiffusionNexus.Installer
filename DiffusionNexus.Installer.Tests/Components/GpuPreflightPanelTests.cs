using Bunit;
using DiffusionNexus.Installer.Core.Modules;
using DiffusionNexus.Installer.Core.Wizard;
using DiffusionNexus.Installer.Electron.Components.Wizard;
using DiffusionNexus.Installer.SDK.Models.Configuration;
using DiffusionNexus.Installer.SDK.Models.Entities;
using DiffusionNexus.Installer.SDK.Services.Hardware;
using FluentAssertions;
using Moq;
using Xunit;

namespace DiffusionNexus.Installer.Tests.Components;

public class GpuPreflightPanelTests : BunitContext
{
    private static async Task<GpuPreflightModule> ModuleAsync(params GpuDetectionResult[] detections)
    {
        var gpu = new Mock<IGpuDetectionService>();
        var sequence = gpu.SetupSequence(g => g.DetectAsync(It.IsAny<CancellationToken>()));
        foreach (var d in detections) sequence = sequence.ReturnsAsync(d);

        var workload = new InstallationConfiguration();
        workload.Repository.Type = RepositoryType.ComfyUI;
        var module = new GpuPreflightModule(gpu.Object);
        await module.InitializeAsync(new WizardSelection { Workload = workload });
        return module;
    }

    [Fact]
    public async Task Without_a_usable_gpu_the_panel_asks_about_the_cpu_build()
    {
        var module = await ModuleAsync(new GpuDetectionResult(GpuDetectionState.NoNvidiaGpu));

        var cut = Render<GpuPreflightPanel>(p => p.Add(x => x.Module, module));

        cut.Find("h2").TextContent.Should().Be("No compatible GPU found");
        cut.FindAll("input[type=checkbox]").Should().HaveCount(1);
    }

    [Fact]
    public async Task A_gpu_found_during_a_side_trip_leaves_nothing_to_answer()
    {
        // #45: the module stays in the plan (applicability is decided at build), so the panel is
        // what tells the user the question no longer applies -- no CPU-build checkbox.
        var module = await ModuleAsync(new GpuDetectionResult(GpuDetectionState.NvidiaGpuWithoutDriver),
                                       new GpuDetectionResult(GpuDetectionState.CudaCapable, "RTX 4090"));
        await module.RefreshAfterResumeAsync();

        var cut = Render<GpuPreflightPanel>(p => p.Add(x => x.Module, module));

        cut.Find("h2").TextContent.Should().Be("Compatible GPU found");
        cut.Markup.Should().Contain("RTX 4090");
        cut.FindAll("input[type=checkbox]").Should().BeEmpty();
    }
}
