using Bunit;
using DiffusionNexus.Installer.Core.Catalog;
using DiffusionNexus.Installer.Core.Content;
using DiffusionNexus.Installer.Core.Host;
using DiffusionNexus.Installer.Core.Install;
using DiffusionNexus.Installer.Core.Modules;
using DiffusionNexus.Installer.Core.Wizard;
using DiffusionNexus.Installer.Electron.Components.Wizard;
using DiffusionNexus.Installer.SDK.Models.Configuration;
using DiffusionNexus.Installer.SDK.Models.Entities;
using DiffusionNexus.Installer.SDK.Models.Installation;
using DiffusionNexus.Installer.SDK.Services;
using DiffusionNexus.Installer.SDK.Services.Settings;
using FluentAssertions;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using Xunit;
// Aliased: the page type is called Install and so is DiffusionNexus.Installer.Core.Install,
// which this file needs for InstallPhase.
using InstallPage = DiffusionNexus.Installer.Electron.Components.Pages.Install;

namespace DiffusionNexus.Installer.Tests.Components;

/// <summary>
/// Renders the real Install page. The whole-branch review found the wizard could not complete a
/// single install — panel edits never re-rendered the parent, so Next stayed disabled — and the fix
/// was five lines of wiring no test covered: deleting any of them would restore the bug with the
/// suite still green. These render the page and drive it the way a user does.
/// </summary>
public class InstallPageTests : BunitContext
{
    private static readonly Guid WorkloadId = Guid.NewGuid();

    private static InstallationConfiguration Workload(string name = "Fooocus")
    {
        var w = new InstallationConfiguration { Id = WorkloadId, Name = name };
        w.Repository.Type = RepositoryType.Fooocus;
        return w;
    }

    private Mock<IInstallSession> Register(InstallationConfiguration workload)
    {
        var settings = new Mock<IUserSettingsRepository>();
        settings.Setup(s => s.GetOrCreateForCurrentUserAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new UserSettings());

        var source = new Mock<IWorkloadSource>();
        source.Setup(s => s.GetInstallerWorkloadsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync([workload]);
        source.Setup(s => s.GetLamaCppWheelsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync([]);

        var session = new Mock<IInstallSession>();
        session.SetupGet(s => s.Phase).Returns(InstallPhase.Idle);
        session.SetupGet(s => s.LogLines).Returns([]);
        session.Setup(s => s.Tail(It.IsAny<int>())).Returns([]);

        Services.AddSingleton(source.Object);
        Services.AddSingleton(session.Object);

        var preflight = new Mock<IModelPreflight>();
        preflight.Setup(p => p.RunAsync(It.IsAny<WizardPlan>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PreflightResult(true, null));
        Services.AddSingleton(preflight.Object);

        Services.AddSingleton(Mock.Of<IUserPrompt>());
        Services.AddSingleton(Mock.Of<IFolderPicker>());
        Services.AddSingleton(new WizardModuleRegistry(() =>
        [
            new InstallFolderModule(settings.Object, new PreInstallationService()),
            new ShortcutsModule(),
            new DisclaimerModule(),
        ]));

        return session;
    }

    [Fact]
    public void Typing_a_folder_re_enables_Next_on_the_page_that_owns_it()
    {
        // The Critical regression. The module's answer lives in a CHILD component; without the
        // page's Changed callback, this page never re-renders and Next stays disabled forever.
        Register(Workload());

        var page = Render<InstallPage>(p => p.Add(x => x.WorkloadId, WorkloadId));

        var next = page.FindAll("button").Single(b => b.TextContent.Trim() == "Next");
        next.HasAttribute("disabled").Should().BeTrue("no install folder has been chosen yet");

        page.Find(".path-row input").Input(@"C:\Installs\Fooocus");

        page.FindAll("button").Single(b => b.TextContent.Trim() == "Next")
            .HasAttribute("disabled").Should().BeFalse("the folder is now set, so the stage validates");
    }

    [Fact]
    public void Every_stage_offers_a_way_back_to_the_gallery()
    {
        // Without this an Electron user -- no address bar, no menu -- is trapped: Back is disabled
        // at stage 0, and a GPU-blocked stage validates false forever so Next never enables.
        Register(Workload());

        var page = Render<InstallPage>(p => p.Add(x => x.WorkloadId, WorkloadId));

        page.FindAll("button").Should().Contain(b => b.TextContent.Trim() == "Cancel");

        page.Find(".wizard-actions button").Click();
        Services.GetRequiredService<NavigationManager>().Uri.Should().EndWith("/");
    }

    [Fact]
    public async Task An_install_of_another_workload_refuses_rather_than_reporting_its_progress()
    {
        // Previously this page built a whole wizard for workload B, walked the user to the Install
        // stage, silently started nothing, and then rendered workload A's progress, log and
        // "Installation complete" banner under B's name.
        var other = new InstallationConfiguration { Id = Guid.NewGuid(), Name = "Workload A" };
        other.Repository.Type = RepositoryType.Fooocus;

        var session = Register(Workload("Workload B"));
        var runningPlan = await new WizardModuleRegistry(() => [])
            .BuildPlanAsync(new WizardSelection { Workload = other });

        session.SetupGet(s => s.Phase).Returns(InstallPhase.Running);
        session.SetupGet(s => s.Plan).Returns(runningPlan);

        var page = Render<InstallPage>(p => p.Add(x => x.WorkloadId, WorkloadId));

        page.Markup.Should().Contain("Another installation is running");
        page.Markup.Should().Contain("Workload A", "the user must be told which install is blocking");
        page.Markup.Should().NotContain("Installing", "workload B's install screen must not be shown");
    }

    [Fact]
    public async Task A_reconnect_to_the_running_workload_rejoins_its_install()
    {
        var workload = Workload();
        var session = Register(workload);

        var runningPlan = await new WizardModuleRegistry(() => [])
            .BuildPlanAsync(new WizardSelection { Workload = workload });

        session.SetupGet(s => s.Phase).Returns(InstallPhase.Running);
        session.SetupGet(s => s.Plan).Returns(runningPlan);

        var page = Render<InstallPage>(p => p.Add(x => x.WorkloadId, WorkloadId));

        page.Markup.Should().Contain("Installing");
        page.Markup.Should().NotContain("Another installation is running");
        session.Verify(s => s.StartAsync(It.IsAny<WizardPlan>(), It.IsAny<CancellationToken>()), Times.Never,
            "the run is already under way; a reconnect must not start a second one");
    }

    [Fact]
    public async Task A_finished_install_offers_the_gallery_back_and_a_running_one_offers_Cancel()
    {
        // The exit from a finished install. It must NOT appear while the run is still going, and
        // the Cancel button must -- which is why it lives in InstallStage, the component that
        // subscribes to Session.Changed, rather than on the page, which never re-renders.
        var workload = Workload();
        var session = Register(workload);

        var runningPlan = await new WizardModuleRegistry(() => [])
            .BuildPlanAsync(new WizardSelection { Workload = workload });

        session.SetupGet(s => s.Phase).Returns(InstallPhase.Running);
        session.SetupGet(s => s.Plan).Returns(runningPlan);

        var page = Render<InstallPage>(p => p.Add(x => x.WorkloadId, WorkloadId));

        page.FindAll("button").Should().Contain(b => b.TextContent.Trim() == "Cancel installation");
        page.FindAll("button").Should().NotContain(b => b.TextContent.Trim() == "Back to workloads");

        session.SetupGet(s => s.Phase).Returns(InstallPhase.Completed);
        session.Raise(s => s.Changed += null);

        page.FindAll("button").Should().Contain(b => b.TextContent.Trim() == "Back to workloads");
        page.FindAll("button").Should().NotContain(b => b.TextContent.Trim() == "Cancel installation");

        page.FindAll("button").Single(b => b.TextContent.Trim() == "Back to workloads").Click();
        Services.GetRequiredService<NavigationManager>().Uri.Should().EndWith("/");
    }

    [Fact]
    public void The_disclaimer_gates_the_confirm_stage()
    {
        // Confirm has no modules but the disclaimer, so without it ValidationErrors is empty there
        // and Next starts an irreversible install with nothing accepted.
        Register(Workload());

        var page = Render<InstallPage>(p => p.Add(x => x.WorkloadId, WorkloadId));

        page.Find(".path-row input").Input(@"C:\Installs\Fooocus");

        // Location -> System -> Confirm.
        while (!page.Markup.Contains("Ready to install"))
        {
            var next = page.FindAll("button").Single(b => b.TextContent.Trim() == "Next");
            next.HasAttribute("disabled").Should().BeFalse();
            next.Click();
        }

        page.Markup.Should().Contain("Software disclaimer");
        page.FindAll("button").Single(b => b.TextContent.Trim() == "Next")
            .HasAttribute("disabled").Should().BeTrue("nothing has been accepted yet");

        page.Find(".checkbox input").Change(true);

        page.FindAll("button").Single(b => b.TextContent.Trim() == "Next")
            .HasAttribute("disabled").Should().BeFalse();
    }

    private static InstallationConfiguration ContentWorkload()
    {
        var w = new InstallationConfiguration { Id = WorkloadId, Name = "Krea-2-Turbo" };
        w.Repository.Type = RepositoryType.ComfyUI;
        w.Repository.RepositoryUrl = "https://github.com/comfyanonymous/ComfyUI";
        w.Vram.VramProfiles = "8,12,16";
        w.ModelDownloads.Add(new ModelDownload { Name = "VAE", Destination = @"models\vae", Url = "https://h.invalid/ae.safetensors" });
        w.Workflows.Add(new ComfUIWorkflow { Name = "1.Text2Image" });
        return w;
    }

    /// <summary>Registers the content workload with a registry that mirrors production's Content stage.</summary>
    private Mock<IInstallSession> RegisterContent(Mock<IModelPresenceScanner> scanner)
    {
        var workload = ContentWorkload();
        var settings = new Mock<IUserSettingsRepository>();
        settings.Setup(s => s.GetOrCreateForCurrentUserAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new UserSettings { DefaultTargetInstallFolder = @"C:\Installs" });

        var source = new Mock<IWorkloadSource>();
        source.Setup(s => s.GetInstallerWorkloadsAsync(It.IsAny<CancellationToken>())).ReturnsAsync([workload]);
        source.Setup(s => s.GetLamaCppWheelsAsync(It.IsAny<CancellationToken>())).ReturnsAsync([]);

        var session = new Mock<IInstallSession>();
        session.SetupGet(s => s.Phase).Returns(InstallPhase.Idle);
        session.SetupGet(s => s.LogLines).Returns([]);
        session.Setup(s => s.Tail(It.IsAny<int>())).Returns([]);

        var preflight = new Mock<IModelPreflight>();
        preflight.Setup(p => p.RunAsync(It.IsAny<WizardPlan>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PreflightResult(true, null));

        var estimator = new Mock<IDiskSpaceEstimator>();
        estimator.Setup(e => e.EstimateAsync(It.IsAny<DiskSpaceRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new DiskSpaceEstimate(1, 2, true, []));

        Services.AddSingleton(source.Object);
        Services.AddSingleton(session.Object);
        Services.AddSingleton(preflight.Object);
        Services.AddSingleton(Mock.Of<IUserPrompt>());
        Services.AddSingleton(Mock.Of<IFolderPicker>());
        Services.AddSingleton(new WizardModuleRegistry(() =>
        [
            new InstallFolderModule(settings.Object, new PreInstallationService()),
            new ComfyFoldersModule(settings.Object),
            new VramProfileModule(),
            new ModelSelectionModule(scanner.Object, estimator.Object),
            new WorkflowSelectionModule(),
            new ShortcutsModule(),
            new DisclaimerModule(),
        ]));

        return session;
    }

    private static Mock<IModelPresenceScanner> EmptyScanner()
    {
        var scanner = new Mock<IModelPresenceScanner>();
        scanner.Setup(s => s.Scan(It.IsAny<ModelScanRequest>())).Returns([]);
        return scanner;
    }

    [Fact]
    public void The_content_stage_renders_its_three_panels_with_the_Changed_callback_wired()
    {
        // Ruling 31 from slice 1: the Changed wiring in RenderModule is what lets a panel edit
        // re-render this page. Deleting any of these three lines leaves every panel test green.
        RegisterContent(EmptyScanner());
        var page = Render<InstallPage>(p => p.Add(x => x.WorkloadId, WorkloadId));

        // Location (folder pre-filled from settings) -> Content.
        page.FindAll("button").Single(b => b.TextContent.Trim() == "Next").Click();

        page.FindComponent<VramProfilePanel>().Instance.Changed.HasDelegate.Should().BeTrue();
        page.FindComponent<ModelSelectionPanel>().Instance.Changed.HasDelegate.Should().BeTrue();
        page.FindComponent<WorkflowSelectionPanel>().Instance.Changed.HasDelegate.Should().BeTrue();
    }

    [Fact]
    public void Changing_the_tier_rescans_the_models_through_the_page()
    {
        // End to end: VRAM panel -> Changed -> page re-render -> ModelSelectionPanel notices -> rescan.
        var scanner = EmptyScanner();
        RegisterContent(scanner);
        var page = Render<InstallPage>(p => p.Add(x => x.WorkloadId, WorkloadId));
        page.FindAll("button").Single(b => b.TextContent.Trim() == "Next").Click();
        var scansBefore = scanner.Invocations.Count(i => i.Method.Name == nameof(IModelPresenceScanner.Scan));

        page.Find("select").Change("16");

        page.FindComponent<ModelSelectionPanel>().Instance.Module.LastScannedTier.Should().Be(16);
        scanner.Invocations.Count(i => i.Method.Name == nameof(IModelPresenceScanner.Scan)).Should().BeGreaterThan(scansBefore);
    }

    [Fact]
    public async Task A_dismissed_preflight_keeps_the_user_on_Confirm()
    {
        RegisterContent(EmptyScanner());
        Services.AddSingleton(Mock.Of<IMismatchedFilePrompt>());
        var preflight = Services.GetRequiredService<IModelPreflight>();
        Mock.Get(preflight).Setup(p => p.RunAsync(It.IsAny<WizardPlan>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PreflightResult(false, null));
        var page = Render<InstallPage>(p => p.Add(x => x.WorkloadId, WorkloadId));

        // Location -> Content -> System -> Confirm.
        while (!page.Markup.Contains("Ready to install"))
            page.FindAll("button").Single(b => b.TextContent.Trim() == "Next").Click();
        page.Find(".checkbox input").Change(true); // disclaimer

        await page.FindAll("button").Single(b => b.TextContent.Trim() == "Next").ClickAsync(new MouseEventArgs());

        page.Markup.Should().Contain("Ready to install", "a dismissed dialog must not advance");
        page.Markup.Should().Contain("not started");
        page.Markup.Should().NotContain("Installing");
    }

    [Fact]
    public void The_confirm_summary_reports_tier_models_and_workflows()
    {
        RegisterContent(EmptyScanner());
        var page = Render<InstallPage>(p => p.Add(x => x.WorkloadId, WorkloadId));

        while (!page.Markup.Contains("Ready to install"))
            page.FindAll("button").Single(b => b.TextContent.Trim() == "Next").Click();

        page.Markup.Should().Contain("8 GB").And.Contain("1 of 1 selected");
    }
}
