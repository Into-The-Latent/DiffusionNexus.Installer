using Bunit;
using DiffusionNexus.Installer.Core.Catalog;
using DiffusionNexus.Installer.Core.Gallery;
using DiffusionNexus.Installer.Core.Host;
using DiffusionNexus.Installer.Core.Install;
using DiffusionNexus.Installer.Core.Modules;
using DiffusionNexus.Installer.Core.Updates;
using DiffusionNexus.Installer.Core.Wizard;
using DiffusionNexus.Installer.SDK.Catalog;
using DiffusionNexus.Installer.SDK.Models.Configuration;
using DiffusionNexus.Installer.SDK.Models.Entities;
using DiffusionNexus.Installer.SDK.Models.Enums;
using DiffusionNexus.Installer.SDK.Shared.Services.Feedback;
using DiffusionNexus.Installer.Tests.Support;
using FluentAssertions;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using InstallPage = DiffusionNexus.Installer.Electron.Components.Pages.Install;
using Xunit;

namespace DiffusionNexus.Installer.Tests.Components;

/// <summary>The wizard page opened on an update, or on several workloads.</summary>
public class InstallPageMergedTests : BunitContext
{
    private static InstallationConfiguration Tiered(string name, string urlPart, params GitRepository[] nodes) => new()
    {
        Id = Guid.NewGuid(),
        Name = name,
        Repository = new MainRepositorySettings { Type = RepositoryType.ComfyUI },
        Vram = new VramSettings { VramProfiles = "8,16,24" },
        GitRepositories = [.. nodes],
        ModelDownloads =
        [
            new ModelDownload
            {
                Name = name + " model",
                DownloadLinks =
                [
                    new ModelDownloadLink { Url = $"https://h/{urlPart}8.gguf", VramProfile = VramProfile.VRAM_8GB, Enabled = true },
                    new ModelDownloadLink { Url = $"https://h/{urlPart}16.gguf", VramProfile = VramProfile.VRAM_16GB, Enabled = true },
                    new ModelDownloadLink { Url = $"https://h/{urlPart}24.gguf", VramProfile = VramProfile.VRAM_24GB, Enabled = true },
                ],
            },
        ],
    };

    private static readonly GitRepository Manager = new() { Name = "Manager", Url = "https://github.com/ltdrdata/ComfyUI-Manager.git" };

    private readonly InstallationConfiguration _a = Tiered("A", "a");
    private readonly InstallationConfiguration _b = Tiered("B", "b", Manager);
    private readonly WorkloadPicks _picks = new();
    private readonly Mock<IInstallSession> _session = new();
    private readonly CapturingModule _capture = new();

    private sealed class CapturingModule : IWizardModule
    {
        public WizardSelection? Selection { get; private set; }
        public string Id => "capture";
        public WizardStage Stage => WizardStage.Confirm;
        public int Order => 0;
        public WorkloadCapability Satisfies => WorkloadCapability.None;
        public bool UsedIn(WizardMode mode) => true;
        public bool AppliesTo(WizardSelection selection) => true;

        public Task InitializeAsync(WizardSelection selection, CancellationToken ct = default)
        {
            Selection = selection;
            return Task.CompletedTask;
        }

        public void Contribute(InstallationOptionsDraft draft) { }
        public ModuleValidation Validate() => ModuleValidation.Ok();
    }

    public InstallPageMergedTests()
    {
        Services.AddSingleton(Mock.Of<IFeedbackReportingService>());
        Services.AddSingleton(OfflineCommunityLinks.Cache());
        Services.AddSingleton(OfflineServerMessages.Cache());
        UpdateSignals.Register(Services);
        JSInterop.Mode = JSRuntimeMode.Loose;

        var registry = new WizardModuleRegistry(() => [_capture]);
        var source = new Mock<IWorkloadSource>();
        source.Setup(s => s.GetInstallerWorkloadsAsync(It.IsAny<CancellationToken>())).ReturnsAsync([_a, _b]);
        source.SetupGet(s => s.Diagnostics).Returns(Array.Empty<CatalogDiagnostic>());
        Services.AddSingleton(source.Object);
        Services.AddSingleton(new GalleryBuilder(source.Object, registry));
        Services.AddSingleton(registry);
        Services.AddSingleton(_session.Object);
        Services.AddSingleton(_picks);
        Services.AddSingleton(Mock.Of<IPostInstallActions>());
        Services.AddSingleton(Mock.Of<IClipboard>());
        Services.AddSingleton(Mock.Of<IUserPrompt>());
        Services.AddSingleton(Mock.Of<IModelPreflight>());
        Services.AddSingleton<ICatalogProvenance>(new FakeCatalogProvenance(() => "test catalog"));
    }

    private string Uri => Services.GetRequiredService<NavigationManager>().Uri;

    private WizardSelection Selection => _capture.Selection!;

    [Fact]
    public void Several_workloads_on_models_only_narrow_each_by_its_own_version()
    {
        _picks.Set(WizardMode.ModelsOnly, [new WorkloadPick(_a.Id, 16), new WorkloadPick(_b.Id, 8)]);

        Render<InstallPage>(p => p.Add(x => x.Mode, WizardMode.ModelsOnly).Add(x => x.Selected, true));

        Selection.Mode.Should().Be(WizardMode.ModelsOnly);
        Selection.Workload.Name.Should().Be("2 workloads");
        Selection.Choices.Should().HaveCount(2);
        Selection.Existing.Should().BeNull();
        Selection.Workload.ModelDownloads.SelectMany(m => m.DownloadLinks).Select(l => l.Url)
            .Should().Equal("https://h/a16.gguf", "https://h/b8.gguf");
        Selection.PackNameByModelId[_b.ModelDownloads[0].Id].Should().Be("B");
    }

    [Fact]
    public void One_pick_at_a_version_still_uses_that_version_and_its_own_name()
    {
        _picks.Set(WizardMode.ModelsOnly, [new WorkloadPick(_a.Id, 8)]);

        Render<InstallPage>(p => p.Add(x => x.Mode, WizardMode.ModelsOnly).Add(x => x.Selected, true));

        Selection.Workload.Name.Should().Be("A");
        Selection.Workload.ModelDownloads.Single().DownloadLinks.Single().Url.Should().Be("https://h/a8.gguf");
    }

    [Theory]
    [InlineData(WizardMode.ModelsOnly, "/existing/models")]
    [InlineData(WizardMode.UpdateExisting, "/existing/update")]
    public void A_refresh_with_no_picks_goes_back_to_the_workload_screen(WizardMode mode, string screen)
    {
        Render<InstallPage>(p => p.Add(x => x.Mode, mode).Add(x => x.Selected, true));

        Uri.Should().EndWith(screen);
        _capture.Selection.Should().BeNull();
    }

    [Fact]
    public void Picks_of_workloads_no_longer_in_the_catalog_are_left_out()
    {
        _picks.Set(WizardMode.ModelsOnly, [new WorkloadPick(Guid.NewGuid(), 8), new WorkloadPick(_a.Id, 8)]);

        Render<InstallPage>(p => p.Add(x => x.Mode, WizardMode.ModelsOnly).Add(x => x.Selected, true));

        Selection.Choices.Should().ContainSingle();
    }

    [Fact]
    public void Several_workloads_on_the_update_path_have_the_runs_state()
    {
        _picks.Set(WizardMode.UpdateExisting, [new WorkloadPick(_a.Id, 16), new WorkloadPick(_b.Id, 24)]);

        Render<InstallPage>(p => p.Add(x => x.Mode, WizardMode.UpdateExisting).Add(x => x.Selected, true));

        Selection.Existing.Should().NotBeNull();
        Selection.Existing!.Packs.Should().Equal(_a, _b);
        Selection.Workload.ModelDownloads.SelectMany(m => m.DownloadLinks).Select(l => l.Url)
            .Should().Equal("https://h/a16.gguf", "https://h/b24.gguf");
    }

    [Fact]
    public void One_workload_on_the_update_path_stays_itself_for_the_memory_panel()
    {
        Render<InstallPage>(p => p.Add(x => x.Mode, WizardMode.UpdateExisting).Add(x => x.WorkloadId, _a.Id));

        Selection.Workload.Id.Should().Be(_a.Id);
        Selection.Workload.Vram.VramProfiles.Should().Be("8,16,24");
        Selection.Workload.ModelDownloads.Single().DownloadLinks.Should().HaveCount(3);
        Selection.IsMultiple.Should().BeFalse();
        Selection.Existing!.Packs.Should().Equal(_a);
    }

    [Fact]
    public void Update_only_adds_no_workload()
    {
        Render<InstallPage>(p => p.Add(x => x.Mode, WizardMode.UpdateExisting).Add(x => x.UpdateOnly, true));

        Selection.UpdateOnly.Should().BeTrue();
        Selection.Workload.Name.Should().Be("ComfyUI update");
        Selection.Workload.ModelDownloads.Should().BeEmpty();
        Selection.Existing!.Packs.Should().BeEmpty();
    }

    [Fact]
    public void A_missing_manager_comes_from_the_catalog_even_on_a_workload_that_has_none()
    {
        Render<InstallPage>(p => p.Add(x => x.Mode, WizardMode.UpdateExisting).Add(x => x.WorkloadId, _a.Id));

        Selection.Existing!.Profile = new SDK.Services.Installation.Existing.ExistingComfyUIProfile(
            @"E:\C", SDK.Services.Installation.Existing.ExistingInstallSupport.Supported, @"E:\C", @"E:\C\venv", "3.12.7");

        Selection.Workload.GitRepositories.Should().ContainSingle().Which.Url.Should().Be(Manager.Url);
    }

    [Fact]
    public void A_fresh_install_is_unchanged()
    {
        Render<InstallPage>(p => p.Add(x => x.WorkloadId, _a.Id));

        Selection.Workload.Should().BeSameAs(_a);
        Selection.Existing.Should().BeNull();
    }

    [Fact]
    public void A_single_models_only_run_is_unchanged()
    {
        Render<InstallPage>(p => p.Add(x => x.Mode, WizardMode.ModelsOnly).Add(x => x.WorkloadId, _a.Id));

        Selection.Workload.Should().BeSameAs(_a);
        Selection.Existing.Should().BeNull();
    }

    private async Task RunningUpdateOf(WizardSelection selection)
    {
        var plan = await new WizardModuleRegistry(() => []).BuildPlanAsync(selection);
        _session.SetupGet(s => s.Phase).Returns(InstallPhase.Running);
        _session.SetupGet(s => s.Plan).Returns(plan);
        _session.SetupGet(s => s.ReportRows).Returns([]);
        _session.Setup(s => s.Tail(It.IsAny<int>())).Returns([]);
    }

    [Fact]
    public async Task A_running_update_blocks_another_wizard_and_shows_its_own_route()
    {
        await RunningUpdateOf(new WizardSelection { Workload = _a, Mode = WizardMode.UpdateExisting, UpdateOnly = true });

        var cut = Render<InstallPage>(p => p.Add(x => x.Mode, WizardMode.UpdateExisting).Add(x => x.WorkloadId, _a.Id));

        cut.Find(".panel-warning h1").TextContent.Should().Be("Another update is running");
        cut.FindAll("button").Single(b => b.TextContent == "Show the running update").Click();

        Uri.Should().EndWith("/existing/update/only");
    }

    [Fact]
    public async Task Other_picks_during_a_running_update_do_not_rejoin_it()
    {
        await RunningUpdateOf(Core.Existing.MergedSelections.ForPicks(WizardMode.UpdateExisting, [new WorkloadPick(_a.Id, 16), new WorkloadPick(_b.Id, 16)], [_a, _b], null)!);
        _picks.Set(WizardMode.UpdateExisting, [new WorkloadPick(_a.Id, 24), new WorkloadPick(_b.Id, 16)]);

        var cut = Render<InstallPage>(p => p.Add(x => x.Mode, WizardMode.UpdateExisting).Add(x => x.Selected, true));

        cut.Find(".panel-warning h1").TextContent.Should().Be("Another update is running");
    }

    [Fact]
    public async Task Show_the_running_update_rejoins_it_even_after_other_picks()
    {
        var picks = new[] { new WorkloadPick(_a.Id, 16), new WorkloadPick(_b.Id, 16) };
        await RunningUpdateOf(Core.Existing.MergedSelections.ForPicks(WizardMode.UpdateExisting, picks, [_a, _b], null)!);
        _picks.Set(WizardMode.UpdateExisting, [new WorkloadPick(_a.Id, 24)]);
        var cut = Render<InstallPage>(p => p.Add(x => x.Mode, WizardMode.UpdateExisting).Add(x => x.Selected, true));

        cut.FindAll("button").Single(b => b.TextContent == "Show the running update").Click();

        cut.FindAll(".panel-warning").Should().BeEmpty();
        _picks.Get(WizardMode.UpdateExisting).Should().Equal(picks);
    }

    [Fact]
    public async Task The_same_picks_rejoin_a_running_update()
    {
        await RunningUpdateOf(Core.Existing.MergedSelections.ForPicks(WizardMode.UpdateExisting, [new WorkloadPick(_a.Id, 16), new WorkloadPick(_b.Id, 16)], [_a, _b], null)!);
        _picks.Set(WizardMode.UpdateExisting, [new WorkloadPick(_a.Id, 16), new WorkloadPick(_b.Id, 16)]);

        var cut = Render<InstallPage>(p => p.Add(x => x.Mode, WizardMode.UpdateExisting).Add(x => x.Selected, true));

        cut.FindAll(".panel-warning").Should().BeEmpty();
    }

    [Fact]
    public async Task The_wizard_of_a_running_update_rejoins_it()
    {
        await RunningUpdateOf(new WizardSelection { Workload = _a, Mode = WizardMode.UpdateExisting, UpdateOnly = true });

        var cut = Render<InstallPage>(p => p.Add(x => x.Mode, WizardMode.UpdateExisting).Add(x => x.UpdateOnly, true));

        cut.FindAll(".panel-warning").Should().BeEmpty();
    }
}
