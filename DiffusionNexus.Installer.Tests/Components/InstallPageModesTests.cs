using Bunit;
using DiffusionNexus.Installer.Core.Catalog;
using DiffusionNexus.Installer.Core.Content;
using DiffusionNexus.Installer.Core.Gallery;
using DiffusionNexus.Installer.Core.Host;
using DiffusionNexus.Installer.Core.Install;
using DiffusionNexus.Installer.Core.Modules;
using DiffusionNexus.Installer.Core.Updates;
using DiffusionNexus.Installer.Core.Wizard;
using DiffusionNexus.Installer.SDK.Catalog;
using DiffusionNexus.Installer.SDK.Models.Configuration;
using DiffusionNexus.Installer.SDK.Models.Entities;
using DiffusionNexus.Installer.SDK.Shared.Services.Feedback;
using DiffusionNexus.Installer.Tests.Support;
using FluentAssertions;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using InstallPage = DiffusionNexus.Installer.Electron.Components.Pages.Install;
using Xunit;

namespace DiffusionNexus.Installer.Tests.Components;

/// <summary>The wizard page on the path it was opened for, with only the disclaimer module registered.</summary>
public class InstallPageModesTests : BunitContext
{
    private static readonly InstallationConfiguration Workload = new()
    {
        Id = Guid.Parse("11111111-1111-1111-1111-111111111111"),
        Name = "The workload",
        Repository = new MainRepositorySettings { Type = RepositoryType.ComfyUI },
        ModelDownloads = [new ModelDownload { Name = "The model", Enabled = true }]
    };

    private readonly Mock<IInstallSession> _session = new();
    private readonly WizardModuleRegistry _registry = new(() => [new DisclaimerModule()]);

    public InstallPageModesTests()
    {
        Services.AddSingleton(Mock.Of<IFeedbackReportingService>());
        Services.AddSingleton(OfflineCommunityLinks.Cache());
        Services.AddSingleton(OfflineServerMessages.Cache());
        UpdateSignals.Register(Services);

        var source = new Mock<IWorkloadSource>();
        source.Setup(s => s.GetInstallerWorkloadsAsync(It.IsAny<CancellationToken>())).ReturnsAsync([Workload]);
        source.SetupGet(s => s.Diagnostics).Returns(Array.Empty<CatalogDiagnostic>());
        Services.AddSingleton(source.Object);
        Services.AddSingleton(new GalleryBuilder(source.Object, _registry));
        Services.AddSingleton(_registry);
        Services.AddSingleton(_session.Object);
        Services.AddSingleton(Mock.Of<IUserPrompt>());
        Services.AddSingleton(Mock.Of<IModelPreflight>());
        Services.AddSingleton<ICatalogProvenance>(new FakeCatalogProvenance(() => "test catalog"));
    }

    private IRenderedComponent<InstallPage> RenderInstall(WizardMode mode) =>
        Render<InstallPage>(p => p.Add(x => x.WorkloadId, Workload.Id).Add(x => x.Mode, mode));

    private async Task RunningInstallOf(WizardMode mode)
    {
        var plan = await _registry.BuildPlanAsync(new WizardSelection { Workload = Workload, Mode = mode });
        _session.SetupGet(s => s.Phase).Returns(InstallPhase.Running);
        _session.SetupGet(s => s.Plan).Returns(plan);
    }

    [Theory]
    [InlineData(WizardMode.FreshInstall)]
    [InlineData(WizardMode.ModelsOnly)]
    public async Task A_running_install_on_the_other_path_blocks_this_one(WizardMode opened)
    {
        await RunningInstallOf(opened == WizardMode.ModelsOnly ? WizardMode.FreshInstall : WizardMode.ModelsOnly);

        var cut = RenderInstall(opened);

        cut.WaitForAssertion(() => cut.Find(".panel-warning h1").TextContent.Should().Be("Another installation is running"));
    }

    [Fact]
    public async Task Show_the_running_install_goes_to_that_runs_own_route()
    {
        await RunningInstallOf(WizardMode.ModelsOnly);
        var cut = RenderInstall(WizardMode.FreshInstall);

        cut.WaitForAssertion(() => cut.Find(".panel-warning .btn-primary").Click());

        Services.GetRequiredService<NavigationManager>().Uri.Should().EndWith($"/existing/models/{Workload.Id}");
    }

    [Theory]
    [InlineData(WizardMode.FreshInstall, "models and workflows")]
    [InlineData(WizardMode.ModelsOnly, "models")]
    public void The_advanced_bar_names_what_is_behind_it(WizardMode mode, string subtitle)
    {
        Services.AddSingleton(new WizardModuleRegistry(() =>
            [new ModelSelectionModule(Mock.Of<IModelPresenceScanner>(), Mock.Of<IDiskSpaceEstimator>())]));

        var cut = RenderInstall(mode);

        cut.WaitForAssertion(() => cut.Find(".advanced-subtitle").TextContent.Trim().Should().Be(subtitle));
    }

    [Theory]
    [InlineData(WizardMode.FreshInstall, "Start installation")]
    [InlineData(WizardMode.ModelsOnly, "Start download")]
    public void The_confirm_button_says_what_it_starts(WizardMode mode, string label)
    {
        var cut = RenderInstall(mode);

        cut.WaitForAssertion(() => cut.Find(".wizard-actions .btn-primary").TextContent.Trim().Should().Be(label));
    }

    [Fact]
    public void The_selection_carries_the_mode_the_page_was_opened_with()
    {
        var kept = Services.GetRequiredService<Electron.Services.ReturnTarget>();
        Services.GetRequiredService<NavigationManager>().NavigateTo(WizardRoutes.Install(Workload.Id, WizardMode.ModelsOnly));

        var cut = RenderInstall(WizardMode.ModelsOnly);

        cut.WaitForAssertion(() => kept.WizardInProgress!.Run.Plan.Selection.Mode.Should().Be(WizardMode.ModelsOnly));
    }
}
