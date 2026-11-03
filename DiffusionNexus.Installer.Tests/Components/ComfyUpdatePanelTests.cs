using Bunit;
using DiffusionNexus.Installer.Core.Content;
using DiffusionNexus.Installer.Core.Existing;
using DiffusionNexus.Installer.Core.Host;
using DiffusionNexus.Installer.Core.Modules;
using DiffusionNexus.Installer.Core.Wizard;
using DiffusionNexus.Installer.Electron.Components.Wizard;
using DiffusionNexus.Installer.SDK.Models.Configuration;
using DiffusionNexus.Installer.SDK.Models.Entities;
using DiffusionNexus.Installer.SDK.Services.Installation.Existing;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using Xunit;

namespace DiffusionNexus.Installer.Tests.Components;

/// <summary>The Update checkbox: the owner's warning, the calm message, and when torch may change.</summary>
public class ComfyUpdatePanelTests : BunitContext
{
    private const string Repo = @"E:\ComfyUI";

    private readonly Mock<IPostInstallActions> _actions = new();

    public ComfyUpdatePanelTests() => Services.AddSingleton(_actions.Object);

    private static InstallationConfiguration Workload(string name, string torch = "2.11.0", string cuda = "13.0") => new()
    {
        Id = Guid.NewGuid(),
        Name = name,
        Repository = new MainRepositorySettings { Type = RepositoryType.ComfyUI },
        Torch = new TorchSettings { TorchVersion = torch, CudaVersion = cuda },
        Python = new PythonEnvironmentSettings { PythonVersion = "3.12" },
    };

    private static ExistingComfyUIProfile Profile(bool manager = true, string? policy = null, string python = "3.12.7") =>
        new(Repo, ExistingInstallSupport.Supported, Repo, Repo + @"\venv", python, new InstalledTorchStack("2.8.0", "12.8", false, false),
            ManagerInstalled: manager, ManagerUpdatePolicy: policy);

    private static async Task<(ComfyUpdateModule Module, WizardSelection Selection)> ModuleAsync(
        ExistingComfyUIProfile? profile = null, bool updateOnly = false, params InstallationConfiguration[] packs)
    {
        var union = ExtendConfigurationMerger.Union(packs.Select(p => new WorkloadChoice(p, 0)).ToList(), "Name");
        var selection = new WizardSelection { Workload = union.Configuration, Mode = WizardMode.UpdateExisting, UpdateOnly = updateOnly };
        selection.Existing = new ExistingRun(selection, union, packs, null);
        var module = new ComfyUpdateModule();
        await module.InitializeAsync(selection);
        selection.Existing.Profile = profile ?? Profile();
        return (module, selection);
    }

    private IRenderedComponent<ComfyUpdatePanel> Panel(ComfyUpdateModule module) =>
        Render<ComfyUpdatePanel>(p => p.Add(x => x.Module, module));

    [Fact]
    public async Task It_starts_on_with_the_warning()
    {
        var (module, _) = await ModuleAsync(packs: Workload("A"));

        var cut = Panel(module);

        module.RunUpdate.Should().BeTrue();
        cut.Find("[data-role=update-warning] h3").TextContent.Should().Be("An update can destroy a working ComfyUI");
        cut.Find("[data-role=update-warning]").TextContent.Should()
            .Contain("If your ComfyUI works today, this is the moment to think twice. After an update:")
            .And.Contain("Custom nodes may stop loading")
            .And.Contain("Workflows you rely on may stop working")
            .And.Contain("There's no automatic way back")
            .And.Contain("Everything working?")
            .And.Contain("Untick this. You still get the workload's custom nodes and models.");
        cut.FindAll("[data-role=update-calm]").Should().BeEmpty();
    }

    [Fact]
    public async Task Unticking_shows_the_calm_message()
    {
        var (module, _) = await ModuleAsync(packs: Workload("A", "2.8.0", "12.8"));
        var cut = Panel(module);

        cut.Find("[data-role=run-update]").Change(false);

        cut.FindAll("[data-role=update-warning]").Should().BeEmpty();
        cut.Find("[data-role=update-calm]").TextContent.Should()
            .Contain("Good choice if it's working.")
            .And.Contain("Your ComfyUI stays on its current version. We only add the workload's custom nodes and models.");
        cut.FindAll("[data-role=torch-note]").Should().BeEmpty();
    }

    [Fact]
    public async Task Unticked_names_the_workloads_that_need_a_newer_torch()
    {
        var (module, _) = await ModuleAsync(packs: [Workload("Wan 2.2 - GGUF"), Workload("Ideogram-4.0"), Workload("Fits", "2.8.0", "12.8")]);
        var cut = Panel(module);

        cut.Find("[data-role=run-update]").Change(false);

        var note = cut.Find("[data-role=torch-note]").TextContent;
        note.Should().Contain("Wan 2.2 - GGUF and Ideogram-4.0 need torch 2.11.0 with CUDA 13.0.");
        note.Should().Contain("You have torch 2.8.0 with CUDA 12.8, and we won't change it, so these workloads may not run until you update.");
        note.Should().NotContain("Fits");
    }

    [Fact]
    public async Task One_workload_reads_as_one()
    {
        var (module, _) = await ModuleAsync(packs: Workload("Wan"));
        var cut = Panel(module);

        cut.Find("[data-role=run-update]").Change(false);

        var note = cut.Find("[data-role=torch-note]").TextContent;
        note.Should().Contain("Wan needs torch 2.11.0 with CUDA 13.0.");
        note.Should().Contain("so this workload may not run until you update.");
    }

    [Fact]
    public async Task Ticked_with_no_usable_gpu_still_names_the_workload_torch_stays_too_old_for()
    {
        var (module, selection) = await ModuleAsync(packs: Workload("Wan"));
        selection.Existing!.GpuUsable = false;

        var note = Panel(module).Find("[data-role=torch-note]").TextContent;

        note.Should().Contain("Wan needs torch 2.11.0 with CUDA 13.0.");
        note.Should().Contain("so this workload may not run.").And.NotContain("until you update");
    }

    [Fact]
    public async Task Ticked_says_nothing_about_a_workload_needing_more()
    {
        var (module, _) = await ModuleAsync(packs: Workload("Wan"));

        Panel(module).FindAll("[data-role=torch-note]").Should().BeEmpty();
    }

    [Fact]
    public async Task Ticked_says_how_torch_changes()
    {
        var (module, _) = await ModuleAsync(packs: Workload("Wan"));

        Panel(module).Find("[data-role=torch-change]").TextContent.Should().Contain("Torch changes from 2.8.0 with CUDA 12.8 to 2.11.0 with CUDA 13.0.");
    }

    [Fact]
    public async Task Workloads_that_differ_are_named()
    {
        var (module, _) = await ModuleAsync(packs: [Workload("Big"), Workload("Small", "2.8.0", "12.8")]);

        Panel(module).Find("[data-role=torch-conflict]").TextContent.Should().Contain("Small");
    }

    [Fact]
    public async Task No_usable_gpu_means_no_torch_change_line()
    {
        var (module, selection) = await ModuleAsync(packs: Workload("Wan"));
        selection.Existing!.GpuUsable = false;

        Panel(module).FindAll("[data-role=torch-change]").Should().BeEmpty();
    }

    [Theory]
    [InlineData(true, null, "To the newest stable release, following your ComfyUI-Manager setting.")]
    [InlineData(true, "stable-comfyui", "To the newest stable release, following your ComfyUI-Manager setting.")]
    [InlineData(true, "nightly-comfyui", "To the latest commit, following your ComfyUI-Manager setting.")]
    [InlineData(false, null, "To the newest stable release. ComfyUI-Manager is missing, so we'll install it.")]
    [InlineData(false, "nightly-comfyui", "To the latest commit, following your ComfyUI-Manager setting.")]
    public async Task The_channel_says_where_the_update_goes(bool manager, string? policy, string expected)
    {
        var (module, _) = await ModuleAsync(Profile(manager, policy), packs: Workload("A"));

        Panel(module).Find("[data-role=channel]").TextContent.Should().Be(expected);
    }

    [Fact]
    public async Task Update_only_is_on_and_locked()
    {
        var (module, _) = await ModuleAsync(updateOnly: true);

        var box = Panel(module).Find("[data-role=run-update]");

        box.HasAttribute("disabled").Should().BeTrue();
        module.RunUpdate.Should().BeTrue();

        module.RunUpdate = false;
        module.RunUpdate.Should().BeTrue("it was chosen on the previous screen");
    }

    [Fact]
    public async Task The_way_out_opens_the_folder()
    {
        var (module, _) = await ModuleAsync(packs: Workload("A"));

        Panel(module).Find("[data-role=open-folder]").Click();

        _actions.Verify(a => a.OpenFolder(Repo), Times.Once);
    }

    [Fact]
    public async Task A_python_with_no_wheel_stops_a_run_that_changes_torch()
    {
        var (module, _) = await ModuleAsync(Profile(python: "3.9.1"), packs: Workload("A"));

        module.Validate().IsValid.Should().BeFalse();
    }

    [Fact]
    public async Task Unticking_clears_the_stop()
    {
        var (module, _) = await ModuleAsync(Profile(python: "3.9.1"), packs: Workload("A"));

        module.RunUpdate = false;

        module.Validate().IsValid.Should().BeTrue();
    }

    [Theory]
    [InlineData(true, true)]
    [InlineData(false, false)]
    public async Task What_the_run_gets(bool update, bool torch)
    {
        var (module, _) = await ModuleAsync(packs: Workload("A"));
        module.RunUpdate = update;
        var draft = new InstallationOptionsDraft { ChangeOutputFolder = true };

        module.Contribute(draft);

        draft.Extend.Should().Be(new ExtendExistingOptions(Repo + @"\venv", update, torch, true));
        draft.CreateDesktopShortcut.Should().BeFalse();
        draft.CreateStartMenuShortcut.Should().BeFalse();
    }

    [Fact]
    public async Task Only_a_new_accelerator_keeps_the_torch_version()
    {
        var workload = Workload("A", "2.8.0", "12.8");
        workload.Python.InstallTriton = true;
        var (module, _) = await ModuleAsync(packs: workload);
        var draft = new InstallationOptionsDraft();

        module.Contribute(draft);

        draft.Extend!.UpgradeTorchStack.Should().BeTrue();
        draft.Extend.KeepTorchVersion.Should().BeTrue();
    }

    [Fact]
    public async Task A_single_workload_on_this_path_starts_at_its_highest_memory_size()
    {
        var module = new VramProfileModule();
        var workload = Workload("A");
        workload.Vram = new VramSettings { VramProfiles = "8,16,24" };

        await module.InitializeAsync(new WizardSelection { Workload = workload, Mode = WizardMode.UpdateExisting });

        module.SelectedTier.Should().Be(24);
    }

    [Fact]
    public void Workflows_are_offered_on_this_path() =>
        new WorkflowSelectionModule().UsedIn(WizardMode.UpdateExisting).Should().BeTrue();

    [Fact]
    public async Task The_scan_uses_the_folder_as_given()
    {
        var workload = Workload("A");
        workload.Repository.RepositoryUrl = "https://github.com/comfyanonymous/ComfyUI";
        workload.ModelDownloads.Add(new ModelDownload { Name = "m", Url = "https://h/m.safetensors" });
        var union = ExtendConfigurationMerger.Union([new WorkloadChoice(workload, 0)]);
        var selection = new WizardSelection { Workload = union.Configuration, Mode = WizardMode.UpdateExisting };
        selection.Existing = new ExistingRun(selection, union, [workload], null);
        var scanner = new Mock<IModelPresenceScanner>();
        scanner.Setup(s => s.Scan(It.IsAny<ModelScanRequest>())).Returns([]);
        var module = new ModelSelectionModule(scanner.Object, Mock.Of<IDiskSpaceEstimator>()) { ScanWait = TimeSpan.FromSeconds(30) };
        await module.InitializeAsync(selection);
        selection.Existing.Profile = new ExistingComfyUIProfile(@"E:\MyComfy", ExistingInstallSupport.Supported, @"E:\MyComfy", @"E:\MyComfy\venv", "3.12.7");

        module.RefreshPresence();

        scanner.Verify(s => s.Scan(It.Is<ModelScanRequest>(r => r.RepositoryPath == @"E:\MyComfy" && !r.DownloadOnly)));
    }

    [Fact]
    public async Task Ticks_survive_going_back_for_another_folder()
    {
        var workload = Workload("A");
        var first = new ModelDownload { Name = "one", Url = "https://h/1.safetensors" };
        workload.ModelDownloads.Add(first);
        workload.ModelDownloads.Add(new ModelDownload { Name = "two", Url = "https://h/2.safetensors" });
        var union = ExtendConfigurationMerger.Union([new WorkloadChoice(workload, 0)]);
        var selection = new WizardSelection { Workload = union.Configuration, Mode = WizardMode.UpdateExisting };
        selection.Existing = new ExistingRun(selection, union, [workload], null);
        var scanner = new Mock<IModelPresenceScanner>();
        scanner.Setup(s => s.Scan(It.IsAny<ModelScanRequest>())).Returns([]);
        var module = new ModelSelectionModule(scanner.Object, Mock.Of<IDiskSpaceEstimator>()) { ScanWait = TimeSpan.FromSeconds(30) };
        await module.InitializeAsync(selection);
        selection.Existing.Profile = Profile();
        module.SetSelected(first.Id, false);

        selection.Existing.Profile = new ExistingComfyUIProfile(@"E:\Second", ExistingInstallSupport.Supported, @"E:\Second", @"E:\Second\venv", "3.12.7");

        module.Rows.Single(r => r.Id == first.Id).IsSelected.Should().BeFalse();
        selection.TargetFolder.Should().Be(@"E:\Second");
    }
}
