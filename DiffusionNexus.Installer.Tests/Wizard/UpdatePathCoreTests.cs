using DiffusionNexus.Installer.Core.Existing;
using DiffusionNexus.Installer.Core.Wizard;
using DiffusionNexus.Installer.SDK.Models.Configuration;
using DiffusionNexus.Installer.SDK.Models.Entities;
using DiffusionNexus.Installer.SDK.Services.Installation.Existing;
using FluentAssertions;
using Xunit;

namespace DiffusionNexus.Installer.Tests.Wizard;

public class UpdatePathCoreTests
{
    private static readonly Guid Id = Guid.Parse("11111111-1111-1111-1111-111111111111");

    private static InstallationConfiguration Workload(string name, string torch = "2.11.0", string cuda = "13.0") => new()
    {
        Id = name == "A" ? Id : Guid.NewGuid(),
        Name = name,
        Repository = new MainRepositorySettings { Type = RepositoryType.ComfyUI },
        Torch = new TorchSettings { TorchVersion = torch, CudaVersion = cuda },
        Python = new PythonEnvironmentSettings { PythonVersion = "3.12" },
        ModelDownloads = [new ModelDownload { Name = "m", DownloadLinks = [new ModelDownloadLink { Url = "https://h/m.safetensors", Enabled = true }] }],
    };

    private static ExistingComfyUIProfile Profile(string folder = @"E:\ComfyUI", string torch = "2.8.0") =>
        new(folder, ExistingInstallSupport.Supported, folder, folder + @"\venv", "3.12.7", new InstalledTorchStack(torch, "12.8", false, false), ManagerInstalled: true);

    private static (WizardSelection Selection, ExistingRun Run, ExtendUnion Union) UpdateRun(params InstallationConfiguration[] packs)
    {
        var union = ExtendConfigurationMerger.Union(packs.Select(p => new WorkloadChoice(p, 0)).ToList(), "Name");
        var selection = new WizardSelection { Workload = union.Configuration, Mode = WizardMode.UpdateExisting };
        var run = new ExistingRun(selection, union, packs, null);
        selection.Existing = run;
        return (selection, run, union);
    }

    [Theory]
    [InlineData(16, 16)]
    [InlineData(32, 32)]
    [InlineData(12, 12)]
    [InlineData(20, 16)]
    public void Memory_picks_the_largest_version_that_fits(int memory, int expected) =>
        WorkloadVersions.ForMemory([8, 12, 16, 24, 32], memory).Should().Be(expected);

    [Fact]
    public void When_no_version_fits_the_smallest_is_kept() =>
        WorkloadVersions.ForMemory([24, 32], 16).Should().Be(24);

    [Fact]
    public void No_versions_gives_zero() =>
        WorkloadVersions.ForMemory([], 16).Should().Be(0);

    [Fact]
    public void A_version_that_is_too_large_says_so_in_these_words() =>
        WorkloadVersions.TooLargeNote(24, 16).Should().Be("This version needs 24 GB, so it may not run on your 16 GB card.");

    [Theory]
    [InlineData(16, 16)]
    [InlineData(8, 16)]
    [InlineData(0, 16)]
    public void A_version_that_fits_says_nothing(int version, int memory) =>
        WorkloadVersions.TooLargeNote(version, memory).Should().BeNull();

    [Fact]
    public void The_update_path_has_its_own_routes()
    {
        WizardRoutes.Install(Id, WizardMode.UpdateExisting).Should().Be($"/existing/update/{Id}");
        WizardRoutes.Workloads(WizardMode.UpdateExisting).Should().Be("/existing/update");
        WizardRoutes.Workloads(WizardMode.ModelsOnly).Should().Be("/existing/models");
        WizardRoutes.Selected(WizardMode.UpdateExisting).Should().Be("/existing/update/selected");
        WizardRoutes.Selected(WizardMode.ModelsOnly).Should().Be("/existing/models/selected");
        WizardRoutes.UpdateOnly.Should().Be("/existing/update/only");
    }

    [Fact]
    public void A_selection_knows_its_route()
    {
        var workload = Workload("A");

        WizardRoutes.For(new WizardSelection { Workload = workload }).Should().Be($"/install/{Id}");
        WizardRoutes.For(new WizardSelection { Workload = workload, Mode = WizardMode.ModelsOnly }).Should().Be($"/existing/models/{Id}");
        WizardRoutes.For(new WizardSelection { Workload = workload, Mode = WizardMode.UpdateExisting }).Should().Be($"/existing/update/{Id}");
        WizardRoutes.For(new WizardSelection { Workload = workload, Mode = WizardMode.UpdateExisting, UpdateOnly = true }).Should().Be("/existing/update/only");
        WizardRoutes.For(new WizardSelection { Workload = workload, Mode = WizardMode.ModelsOnly, Choices = [new WorkloadChoice(workload, 16)] })
            .Should().Be("/existing/models/selected");
    }

    [Fact]
    public void Picks_are_kept_per_path()
    {
        var picks = new WorkloadPicks();
        picks.Set(WizardMode.ModelsOnly, [new WorkloadPick(Id, 16)]);

        picks.Get(WizardMode.ModelsOnly).Should().Equal(new WorkloadPick(Id, 16));
        picks.Get(WizardMode.UpdateExisting).Should().BeEmpty();
    }

    [Theory]
    [InlineData(true, true, true)]
    [InlineData(false, true, false)]
    [InlineData(true, false, false)]
    [InlineData(false, false, false)]
    public void Torch_only_changes_when_ticked_with_a_usable_gpu(bool update, bool gpu, bool expected)
    {
        var (_, run, _) = UpdateRun(Workload("A"));
        run.Profile = Profile();
        run.RunUpdate = update;
        run.GpuUsable = gpu;

        run.UpgradeTorch.Should().Be(expected);
    }

    [Fact]
    public void Nothing_to_change_is_no_upgrade_even_when_ticked()
    {
        var (_, run, _) = UpdateRun(Workload("A", "2.8.0", "12.8"));
        run.Profile = Profile();

        run.UpgradeTorch.Should().BeFalse();
    }

    [Fact]
    public void Before_the_folder_is_known_nothing_changes()
    {
        var (selection, run, union) = UpdateRun(Workload("A"));

        run.UpgradeTorch.Should().BeFalse();
        run.Target.Should().BeNull();
        selection.Workload.Should().BeSameAs(union.Configuration);
    }

    [Fact]
    public void The_profile_sets_the_folder_and_the_merged_torch()
    {
        var (selection, run, _) = UpdateRun(Workload("A"));

        run.Profile = Profile();

        selection.TargetFolder.Should().Be(@"E:\ComfyUI");
        selection.Workload.Torch.TorchVersion.Should().Be("2.11.0");
    }

    [Fact]
    public void Unticking_puts_torch_back_to_what_is_installed()
    {
        var (selection, run, _) = UpdateRun(Workload("A"));
        run.Profile = Profile();

        run.RunUpdate = false;

        selection.Workload.Torch.TorchVersion.Should().Be("2.8.0");
    }

    [Fact]
    public void No_usable_gpu_puts_torch_back_to_what_is_installed()
    {
        var (selection, run, _) = UpdateRun(Workload("A"));
        run.Profile = Profile();

        run.GpuUsable = false;

        selection.Workload.Torch.TorchVersion.Should().Be("2.8.0");
    }

    [Fact]
    public void The_model_list_survives_every_rewrite()
    {
        var (selection, run, union) = UpdateRun(Workload("A"));
        var models = selection.Workload.ModelDownloads;

        run.Profile = Profile();
        run.RunUpdate = false;
        run.Profile = Profile(@"E:\Other");

        selection.Workload.ModelDownloads.Should().BeSameAs(models);
        selection.Workload.Id.Should().Be(union.Configuration.Id);
    }

    [Fact]
    public void Another_folder_replaces_what_the_first_one_said()
    {
        var (selection, run, _) = UpdateRun(Workload("A"));
        run.Profile = Profile();

        run.Profile = Profile(@"E:\Other", torch: "2.13.0");

        selection.TargetFolder.Should().Be(@"E:\Other");
        run.Target!.ChangesInstalledStack.Should().BeFalse();
        selection.Workload.Torch.TorchVersion.Should().Be("2.13.0");
    }

    [Fact]
    public void An_unsupported_folder_clears_the_answer()
    {
        var (selection, run, union) = UpdateRun(Workload("A"));
        run.Profile = Profile();

        run.Profile = new ExistingComfyUIProfile(@"E:\Nope", ExistingInstallSupport.NotComfyUI);

        run.Target.Should().BeNull();
        selection.TargetFolder.Should().BeEmpty();
        selection.Workload.Should().BeSameAs(union.Configuration);
    }

    [Fact]
    public void Once_the_install_has_started_a_late_answer_changes_nothing()
    {
        var (selection, run, _) = UpdateRun(Workload("A"));
        run.Profile = Profile();
        var workload = selection.Workload;
        run.Freeze();
        var announced = 0;
        run.Changed += () => announced++;

        run.Profile = null;
        run.RunUpdate = false;
        run.GpuUsable = false;
        run.SetFolderChanges(["late"]);

        selection.Workload.Should().BeSameAs(workload);
        selection.TargetFolder.Should().Be(@"E:\ComfyUI");
        run.UpgradeTorch.Should().BeTrue();
        run.FolderChangeLines.Should().BeEmpty();
        announced.Should().Be(0);
    }

    [Fact]
    public async Task Reaching_the_install_stage_is_what_starts_it()
    {
        var (selection, run, _) = UpdateRun(Workload("A"));
        var plan = await new WizardModuleRegistry(() => []).BuildPlanAsync(selection);
        run.Profile = Profile();
        var wizard = new WizardRun(plan);

        while (wizard.CurrentStage != WizardStage.Install)
        {
            run.Profile = Profile();
            wizard.TryNext().Should().BeTrue();
        }

        run.Profile = null;

        run.Profile.Should().NotBeNull();
    }

    [Fact]
    public void Every_change_is_announced()
    {
        var (_, run, _) = UpdateRun(Workload("A"));
        var count = 0;
        run.Changed += () => count++;

        run.Profile = Profile();
        run.RunUpdate = false;
        run.GpuUsable = false;

        count.Should().Be(3);
    }

    [Fact]
    public void A_compatibility_error_only_stops_a_run_that_changes_torch()
    {
        var (_, run, _) = UpdateRun(Workload("A"));
        run.Profile = Profile() with { PythonVersion = "3.9.1" };

        run.TorchHardStop.Should().NotBeEmpty();

        run.RunUpdate = false;

        run.TorchHardStop.Should().BeEmpty();
    }

    private sealed class StageModule(string id, WizardStage stage, Func<bool> applies) : IWizardModule
    {
        public string Id => id;
        public WizardStage Stage => stage;
        public int Order => 0;
        public WorkloadCapability Satisfies => WorkloadCapability.None;
        public bool UsedIn(WizardMode mode) => true;
        public bool AppliesTo(WizardSelection selection) => applies();
        public Task InitializeAsync(WizardSelection selection, CancellationToken ct = default) => Task.CompletedTask;
        public void Contribute(InstallationOptionsDraft draft) { }
        public ModuleValidation Validate() => ModuleValidation.Ok();
    }

    private static Task<WizardPlan> PlanAsync(WizardMode mode, Func<bool> systemApplies) =>
        new WizardModuleRegistry(() =>
        [
            new StageModule("l", WizardStage.Location, () => true),
            new StageModule("c", WizardStage.Content, () => true),
            new StageModule("s", WizardStage.System, systemApplies),
        ]).BuildPlanAsync(new WizardSelection { Workload = Workload("A"), Mode = mode });

    [Fact]
    public async Task On_the_update_path_a_stage_follows_what_was_answered()
    {
        var applies = false;
        var run = new WizardRun(await PlanAsync(WizardMode.UpdateExisting, () => applies));

        run.Plan.Stages.Should().NotContain(WizardStage.System);

        applies = true;
        run.TryNext();
        run.CurrentStage.Should().Be(WizardStage.Content);
        run.TryNext();

        run.CurrentStage.Should().Be(WizardStage.System);
    }

    [Fact]
    public async Task A_stage_that_goes_away_while_on_it_does_not_skip_the_next()
    {
        var applies = true;
        var run = new WizardRun(await PlanAsync(WizardMode.UpdateExisting, () => applies));
        run.TryNext();
        run.TryNext();
        run.CurrentStage.Should().Be(WizardStage.System);

        applies = false;
        run.TryNext();

        run.CurrentStage.Should().Be(WizardStage.Confirm);
    }

    [Fact]
    public async Task Back_lands_on_the_stage_before_after_a_refresh()
    {
        var applies = true;
        var run = new WizardRun(await PlanAsync(WizardMode.UpdateExisting, () => applies));
        run.TryNext();
        applies = false;
        run.TryNext();
        run.CurrentStage.Should().Be(WizardStage.Confirm);

        run.Back();

        run.CurrentStage.Should().Be(WizardStage.Content);
    }

    [Fact]
    public async Task A_fresh_install_keeps_the_stages_it_was_built_with()
    {
        var applies = false;
        var run = new WizardRun(await PlanAsync(WizardMode.FreshInstall, () => applies));

        applies = true;
        run.TryNext();
        run.TryNext();

        run.CurrentStage.Should().Be(WizardStage.Confirm);
    }
}
