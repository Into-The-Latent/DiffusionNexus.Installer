using Bunit;
using DiffusionNexus.Installer.Core.Wizard;
using DiffusionNexus.Installer.Electron.Components.Wizard;
using DiffusionNexus.Installer.SDK.Models.Configuration;
using FluentAssertions;
using Xunit;

namespace DiffusionNexus.Installer.Tests.Components;

public class ConfirmStageTests : BunitContext
{
    private static async Task<WizardRun> RunAsync(WizardMode mode, bool sort = true)
    {
        var workload = new InstallationConfiguration { Name = "The workload" };
        workload.Repository.Type = RepositoryType.ComfyUI;

        var plan = await new WizardModuleRegistry(() => [])
            .BuildPlanAsync(new WizardSelection { Workload = workload, Mode = mode });
        plan.Selection.TargetFolder = @"E:\AI\models";
        plan.Selection.SortModelsIntoSubfolders = sort;
        return new WizardRun(plan, WizardStage.Confirm);
    }

    [Fact]
    public async Task Models_only_shows_the_folder_and_the_switch()
    {
        var run = await RunAsync(WizardMode.ModelsOnly);

        var cut = Render<ConfirmStage>(p => p.Add(x => x.Run, run));

        cut.Find("h2").TextContent.Should().Be("Ready to download");
        cut.Markup.Should().Contain("Download folder").And.Contain(@"E:\AI\models");
        cut.Find("[data-role=subfolders]").TextContent.Should().Contain("On");
    }

    [Fact]
    public async Task Models_only_says_when_subfolders_are_off()
    {
        var run = await RunAsync(WizardMode.ModelsOnly, sort: false);

        var cut = Render<ConfirmStage>(p => p.Add(x => x.Run, run));

        cut.Find("[data-role=subfolders]").TextContent.Should().Contain("Off");
    }

    [Fact]
    public async Task Models_only_leaves_out_what_it_does_not_install()
    {
        var run = await RunAsync(WizardMode.ModelsOnly);

        var cut = Render<ConfirmStage>(p => p.Add(x => x.Run, run));

        var rows = cut.FindAll("dt").Select(d => d.TextContent).ToList();
        rows.Should().NotContain(["Software", "Install folder", "Python", "Shortcuts"]);
        rows.Should().Contain("Workload");
    }

    [Fact]
    public async Task A_fresh_install_keeps_its_summary()
    {
        var run = await RunAsync(WizardMode.FreshInstall);

        var cut = Render<ConfirmStage>(p => p.Add(x => x.Run, run));

        cut.Find("h2").TextContent.Should().Be("Ready to install");
        var rows = cut.FindAll("dt").Select(d => d.TextContent).ToList();
        rows.Should().Contain(["Workload", "Software", "Install folder", "Python", "Shortcuts"]);
        cut.FindAll("[data-role=subfolders]").Should().BeEmpty();
    }
}
