using Bunit;
using DiffusionNexus.Installer.Core.Modules;
using DiffusionNexus.Installer.Core.Wizard;
using DiffusionNexus.Installer.Electron.Components.Wizard;
using DiffusionNexus.Installer.SDK.Models.Configuration;
using DiffusionNexus.Installer.SDK.Services.Hardware;
using FluentAssertions;
using Moq;
using Xunit;

namespace DiffusionNexus.Installer.Tests.Components;

public class VcRuntimePanelTests : BunitContext
{
    private static async Task<VcRuntimeModule> ModuleAsync(params VcRuntimeState[] detections)
    {
        var detection = new Mock<IVcRuntimeDetectionService>();
        var sequence = detection.SetupSequence(d => d.Detect());
        foreach (var state in detections)
            sequence = sequence.Returns(new VcRuntimeDetectionResult(state, state == VcRuntimeState.Present ? new Version(14, 44) : null));

        var module = new VcRuntimeModule(detection.Object);
        await module.InitializeAsync(new WizardSelection { Workload = new InstallationConfiguration() });
        return module;
    }

    [Fact]
    public async Task A_missing_runtime_asks_whether_to_install_it()
    {
        var module = await ModuleAsync(VcRuntimeState.Missing);

        var cut = Render<VcRuntimePanel>(p => p.Add(x => x.Module, module));

        cut.Find("h2").TextContent.Should().Be("Visual C++ runtime is missing");
        cut.FindAll("input[type=checkbox]").Should().HaveCount(1);
    }

    [Fact]
    public async Task A_runtime_installed_during_a_side_trip_leaves_nothing_to_answer()
    {
        // #45: the module stays in the plan (applicability is decided at build), so the panel is
        // what tells the user the question no longer applies -- no checkbox, no UAC warning.
        var module = await ModuleAsync(VcRuntimeState.Missing, VcRuntimeState.Present);
        module.RefreshAfterResume();

        var cut = Render<VcRuntimePanel>(p => p.Add(x => x.Module, module));

        cut.Find("h2").TextContent.Should().Be("Visual C++ runtime is installed");
        cut.Markup.Should().Contain("14.44");
        cut.FindAll("input[type=checkbox]").Should().BeEmpty();
    }
}
