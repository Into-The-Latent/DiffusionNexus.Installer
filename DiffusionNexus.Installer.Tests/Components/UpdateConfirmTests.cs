using Bunit;
using DiffusionNexus.Installer.Core.Content;
using DiffusionNexus.Installer.Core.Existing;
using DiffusionNexus.Installer.Core.Host;
using DiffusionNexus.Installer.Core.Install;
using DiffusionNexus.Installer.Core.Modules;
using DiffusionNexus.Installer.Core.Wizard;
using DiffusionNexus.Installer.Electron.Components.Wizard;
using DiffusionNexus.Installer.SDK.Models.Configuration;
using DiffusionNexus.Installer.SDK.Models.Entities;
using DiffusionNexus.Installer.SDK.Services;
using DiffusionNexus.Installer.SDK.Services.Installation.Existing;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using Xunit;

namespace DiffusionNexus.Installer.Tests.Components;

/// <summary>What Confirm and the result say on the update path, and on several workloads.</summary>
public sealed class UpdateConfirmTests : BunitContext, IDisposable
{
    private readonly string _repo = Path.Combine(Path.GetTempPath(), "UpdateConfirm_" + Guid.NewGuid().ToString("N"));
    private readonly Mock<IInstallSession> _session = new();

    public UpdateConfirmTests()
    {
        Directory.CreateDirectory(_repo);
        JSInterop.Mode = JSRuntimeMode.Loose;
        Services.AddSingleton(_session.Object);
        Services.AddSingleton(Mock.Of<IPostInstallActions>());
        Services.AddSingleton(Mock.Of<IClipboard>());
        _session.SetupGet(s => s.ReportRows).Returns([]);
        _session.Setup(s => s.Tail(It.IsAny<int>())).Returns([]);
    }

    void IDisposable.Dispose()
    {
        base.Dispose();
        try
        {
            Directory.Delete(_repo, recursive: true);
        }
        catch
        {
            // best-effort cleanup
        }
    }

    private static InstallationConfiguration Workload(string name, string tiers = "8,16,24", string torch = "2.11.0") => new()
    {
        Id = Guid.NewGuid(),
        Name = name,
        Repository = new MainRepositorySettings { Type = RepositoryType.ComfyUI },
        Torch = new TorchSettings { TorchVersion = torch, CudaVersion = "13.0" },
        Python = new PythonEnvironmentSettings { PythonVersion = "3.12" },
        Vram = new VramSettings { VramProfiles = tiers },
    };

    private ExistingComfyUIProfile Profile() =>
        new(_repo, ExistingInstallSupport.Supported, _repo, _repo + @"\venv", "3.12.7", new InstalledTorchStack("2.8.0", "12.8", false, false),
            ManagerInstalled: true, UpdateScript: UpdateScriptState.Legacy);

    private async Task<WizardRun> UpdateRunAsync(bool updateOnly = false, params WorkloadChoice[] choices)
    {
        var selection = updateOnly
            ? MergedSelections.ForUpdateOnly([], null)
            : MergedSelections.ForPicks(WizardMode.UpdateExisting, choices.Select(c => new WorkloadPick(c.Workload.Id, c.VramGb)).ToList(), choices.Select(c => c.Workload).ToList(), null)!;
        var review = new ExistingReviewModule();
        var plan = await new WizardModuleRegistry(() => [new ComfyUpdateModule(), review, new DisclaimerModule()]).BuildPlanAsync(selection);
        selection.Existing!.Profile = Profile();
        return new WizardRun(plan);
    }

    private static ExistingReviewModule Review(WizardRun run) => run.Plan.AllModules.OfType<ExistingReviewModule>().Single();

    private IRenderedComponent<ExistingReviewPanel> ReviewPanel(WizardRun run) =>
        Render<ExistingReviewPanel>(p => p.Add(x => x.Module, Review(run)));

    [Fact]
    public async Task Next_waits_for_the_tick()
    {
        var run = await UpdateRunAsync(false, new WorkloadChoice(Workload("A"), 16));

        Review(run).Validate().IsValid.Should().BeFalse();

        ReviewPanel(run).Find("[data-role=understood]").Change(true);

        Review(run).Validate().IsValid.Should().BeTrue();
    }

    [Fact]
    public async Task A_folder_that_stopped_being_usable_stops_the_start()
    {
        var run = await UpdateRunAsync(false, new WorkloadChoice(Workload("A"), 16));
        Review(run).Understood = true;
        run.Plan.Selection.Existing!.Profile = new ExistingComfyUIProfile(_repo, ExistingInstallSupport.NoVenv);
        Review(run).Understood = true;

        Review(run).Validate().IsValid.Should().BeFalse();
    }

    [Fact]
    public async Task A_folder_that_stopped_being_usable_renders_its_message_not_a_crash()
    {
        var a = Workload("A");
        a.GitRepositories.Add(new GitRepository { Name = "Node", Url = "https://github.com/x/node.git" });
        var run = await UpdateRunAsync(false, new WorkloadChoice(a, 16));

        run.Plan.Selection.Existing!.Profile = new ExistingComfyUIProfile(_repo, ExistingInstallSupport.NoVenv);

        Review(run).Lines.Should().BeEmpty();
        ReviewPanel(run);
    }

    [Fact]
    public async Task Confirm_waits_for_the_check_a_side_trip_started_and_shows_its_answer()
    {
        var run = await UpdateRunAsync(false, new WorkloadChoice(Workload("A"), 16));
        var existing = run.Plan.Selection.Existing!;
        var check = new TaskCompletionSource();
        var refreshed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var armed = false;
        var cut = Render<ExistingReviewPanel>(p => p
            .Add(x => x.Module, Review(run))
            .Add(x => x.Changed, Microsoft.AspNetCore.Components.EventCallback.Factory.Create(this, () =>
            {
                if (armed) refreshed.TrySetResult();
            })));
        cut.Find("[data-role=understood]").Change(true);
        armed = true;

        existing.Checking(check.Task);

        Review(run).Validate().ErrorMessage.Should().Contain("Checking your ComfyUI folder");

        existing.Profile = Profile() with { UpdateScript = UpdateScriptState.Current };
        check.SetResult();

        // Awaited, not asserted inside a wait: the panel renders before it calls the page, so a wait
        // that only looks again after a render can look too early and never look again.
        await refreshed.Task.WaitAsync(TimeSpan.FromSeconds(10));
        cut.WaitForAssertion(() => cut.Find("[data-role=understood]").HasAttribute("checked").Should().BeFalse());
    }

    [Fact]
    public async Task A_torch_change_that_cannot_work_stops_the_start_even_when_it_appeared_on_this_screen()
    {
        var run = await UpdateRunAsync(false, new WorkloadChoice(Workload("A"), 16));
        var existing = run.Plan.Selection.Existing!;
        existing.GpuUsable = false;
        existing.Profile = Profile() with { PythonVersion = "3.9.1" };
        Review(run).Understood = true;

        existing.GpuUsable = true;
        Review(run).Understood = true;

        Review(run).Validate().IsValid.Should().BeFalse();
    }

    [Fact]
    public async Task An_answer_that_changes_nothing_on_the_list_keeps_the_tick()
    {
        var run = await UpdateRunAsync(false, new WorkloadChoice(Workload("A"), 16));
        Review(run).Understood = true;

        run.Plan.Selection.Existing!.Profile = Profile() with { PythonVersion = "3.12.8" };

        Review(run).Understood.Should().BeTrue();
    }

    [Fact]
    public async Task A_node_installed_during_a_side_trip_comes_off_the_list_when_the_check_answers()
    {
        var a = Workload("A");
        a.GitRepositories.Add(new GitRepository { Name = "Node", Url = "https://github.com/x/node.git" });
        var run = await UpdateRunAsync(false, new WorkloadChoice(a, 16));
        var existing = run.Plan.Selection.Existing!;
        existing.Profile = Profile();
        Review(run).Lines.Should().Contain("Custom nodes: 1 to add.");
        Directory.CreateDirectory(Path.Combine(_repo, "custom_nodes", "node"));

        existing.CheckedAgain();

        Review(run).Lines.Should().NotContain(l => l.StartsWith("Custom nodes"));
    }

    [Fact]
    public async Task A_new_manager_is_named_among_the_nodes_to_add()
    {
        var a = Workload("A");
        a.GitRepositories.Add(new GitRepository { Name = "ComfyUI-Manager", Url = "https://github.com/ltdrdata/ComfyUI-Manager.git" });
        var run = await UpdateRunAsync(false, new WorkloadChoice(a, 16));

        run.Plan.Selection.Existing!.Profile = Profile() with { ManagerInstalled = false };

        Review(run).Lines.Should().Contain("Custom nodes: 1 to add, including ComfyUI-Manager.");
    }

    [Fact]
    public async Task A_warned_model_row_on_an_update_is_not_green()
    {
        Finished(InstallationResult.Success("ok", _repo));
        _session.SetupGet(s => s.ReportRows).Returns(
        [
            new SDK.Models.Installation.InstallReportEntry
            {
                PlannedOperation = "Model: Second",
                Category = SDK.Models.Installation.InstallReportCategory.Model,
                Outcome = SDK.Models.Installation.InstallReportOutcome.Skipped,
                Comment = "Skipped: another model in this run has the same file name.",
                IsWarning = true,
            },
        ]);
        var run = await UpdateRunAsync(false, new WorkloadChoice(Workload("A"), 16));

        var heading = Render<InstallStage>(p => p.Add(x => x.Run, run)).Find(".install-result h2");

        heading.TextContent.Trim().Should().Be("Finished with warnings");
        heading.ClassList.Should().Contain("result-bad");
    }

    [Fact]
    public async Task Only_a_new_accelerator_does_not_claim_torch_changes()
    {
        var a = Workload("A", torch: "2.8.0");
        a.Torch.CudaVersion = "12.8";
        a.Python.InstallTriton = true;
        var run = await UpdateRunAsync(false, new WorkloadChoice(a, 16));

        Review(run).Lines.Should().Contain("Torch: stays at 2.8.0 with CUDA 12.8, and Triton is added.");
        ReviewPanel(run).Find("[data-role=confirm-torch]").TextContent.Should().Be("Triton is added to torch 2.8.0 with CUDA 12.8.");
    }

    [Fact]
    public async Task A_torch_that_could_not_be_read_says_it_is_left_alone()
    {
        var run = await UpdateRunAsync(false, new WorkloadChoice(Workload("A"), 16));

        run.Plan.Selection.Existing!.Profile = Profile() with { Torch = null };

        Review(run).Lines.Should().Contain("Torch: could not be read, so it is left as it is.");
    }

    [Fact]
    public async Task Confirm_says_to_close_comfyui()
    {
        var run = await UpdateRunAsync(false, new WorkloadChoice(Workload("A"), 16));

        ReviewPanel(run).Find("[data-role=close-comfyui]").TextContent.Should().Contain("Close ComfyUI");
    }

    [Fact]
    public async Task A_llama_workload_says_what_it_changes()
    {
        var a = Workload("A");
        a.SelectedLamaCppWheelId = Guid.NewGuid();
        var run = await UpdateRunAsync(false, new WorkloadChoice(a, 16));
        run.Plan.Selection.Existing!.RunUpdate = false;

        Review(run).Lines.Should().Contain(l => l.StartsWith("llama.cpp:") && l.Contains("numpy"));
    }

    [Fact]
    public async Task A_changed_answer_asks_again()
    {
        var run = await UpdateRunAsync(false, new WorkloadChoice(Workload("A"), 16));
        Review(run).Understood = true;

        run.Plan.Selection.Existing!.RunUpdate = false;

        Review(run).Understood.Should().BeFalse();
    }

    [Fact]
    public async Task Ticked_with_no_usable_gpu_says_the_workload_may_not_run()
    {
        var run = await UpdateRunAsync(false, new WorkloadChoice(Workload("A"), 16));

        run.Plan.Selection.Existing!.GpuUsable = false;

        ReviewPanel(run).Find("[data-role=confirm-note]").TextContent.Should().Contain("A needs torch 2.11.0");
    }

    [Fact]
    public async Task The_folders_the_run_changes_are_listed_and_a_new_answer_asks_again()
    {
        var run = await UpdateRunAsync(false, new WorkloadChoice(Workload("A"), 16));
        Review(run).Understood = true;

        run.Plan.Selection.Existing!.FolderChanges = () => ["Output folder: changes."];

        Review(run).Lines.Should().Contain("Output folder: changes.");
        Review(run).Understood.Should().BeFalse();
    }

    [Fact]
    public async Task The_same_folder_answer_does_not_ask_again()
    {
        var run = await UpdateRunAsync(false, new WorkloadChoice(Workload("A"), 16));
        run.Plan.Selection.Existing!.FolderChanges = () => ["Output folder: changes."];
        Review(run).Understood = true;

        run.Plan.Selection.Existing.FolderChanges = () => ["Output folder: changes."];

        Review(run).Understood.Should().BeTrue();
    }

    [Fact]
    public async Task The_list_says_what_changes_for_two_workloads()
    {
        var a = Workload("A");
        a.GitRepositories = [new GitRepository { Url = "https://github.com/x/NodeA" }, new GitRepository { Url = "https://github.com/x/Present" }];
        Directory.CreateDirectory(Path.Combine(_repo, "custom_nodes", "Present"));
        var run = await UpdateRunAsync(false, new WorkloadChoice(a, 16), new WorkloadChoice(Workload("B"), 8));

        Review(run).Lines.Should().Contain("ComfyUI: updates to the newest stable release, following your ComfyUI-Manager setting.");
        Review(run).Lines.Should().Contain("Torch: changes from 2.8.0 with CUDA 12.8 to 2.11.0 with CUDA 13.0.");
        Review(run).Lines.Should().Contain("Custom nodes: 1 to add.");
        Review(run).Lines.Should().Contain("Update script: the old one is replaced.");
    }

    [Fact]
    public async Task The_list_for_update_only()
    {
        var run = await UpdateRunAsync(updateOnly: true);

        Review(run).Lines.Should().Contain(l => l.StartsWith("ComfyUI: updates"));
        Review(run).Lines.Should().Contain("Torch: stays as it is.");
        Review(run).Lines.Should().NotContain(l => l.StartsWith("Custom nodes"));
    }

    [Fact]
    public async Task Unticked_says_comfyui_stays()
    {
        var run = await UpdateRunAsync(false, new WorkloadChoice(Workload("A"), 16));
        run.Plan.Selection.Existing!.RunUpdate = false;

        Review(run).Lines.Should().Contain("ComfyUI: stays on its current version.");
        Review(run).Lines.Should().Contain("Torch: stays as it is.");
    }

    [Fact]
    public async Task The_red_boxes_follow_what_was_chosen()
    {
        var run = await UpdateRunAsync(false, new WorkloadChoice(Workload("A"), 16));

        var cut = ReviewPanel(run);

        cut.Find("[data-role=may-break]").TextContent.Should().Contain("your ComfyUI may stop working after this update");
        cut.FindAll("[data-role=confirm-torch]").Should().ContainSingle();
        cut.FindAll("[data-role=confirm-note]").Should().BeEmpty();

        run.Plan.Selection.Existing!.RunUpdate = false;
        cut = ReviewPanel(run);

        cut.FindAll("[data-role=may-break]").Should().BeEmpty();
        cut.FindAll("[data-role=confirm-torch]").Should().BeEmpty();
        cut.Find("[data-role=confirm-note]").TextContent.Should().Contain("A needs torch 2.11.0");
    }

    private IRenderedComponent<ConfirmStage> Confirm(WizardRun run) => Render<ConfirmStage>(p => p.Add(x => x.Run, run));

    [Fact]
    public async Task Confirm_on_the_update_path_lists_the_folder_and_each_version()
    {
        var run = await UpdateRunAsync(false, new WorkloadChoice(Workload("A"), 16), new WorkloadChoice(Workload("B", tiers: string.Empty), 0));

        var cut = Confirm(run);

        cut.Find("h2").TextContent.Should().Be("Ready to update");
        cut.Markup.Should().Contain("ComfyUI folder").And.Contain(_repo);
        cut.Markup.Should().NotContain("Install folder").And.NotContain("Shortcuts");
        cut.FindAll("[data-role=version]").Select(v => v.TextContent.Trim()).Should().Equal("A: 16 GB", "B: one version only");
        cut.Find("[data-role=comfy-update]").TextContent.Should().Be("On");
    }

    [Fact]
    public async Task Confirm_for_update_only_says_so()
    {
        var cut = Confirm(await UpdateRunAsync(updateOnly: true));

        cut.Find("[data-role=no-workloads]").TextContent.Should().Be("None. This only updates ComfyUI.");
    }

    [Fact]
    public async Task Confirm_on_models_only_lists_each_version_too()
    {
        var a = Workload("A");
        var b = Workload("B");
        var selection = MergedSelections.ForPicks(WizardMode.ModelsOnly, [new WorkloadPick(a.Id, 8), new WorkloadPick(b.Id, 24)], [a, b], null)!;
        var plan = await new WizardModuleRegistry(() => [new DisclaimerModule()]).BuildPlanAsync(selection);

        var cut = Confirm(new WizardRun(plan));

        cut.FindAll("[data-role=version]").Select(v => v.TextContent.Trim()).Should().Equal("A: 8 GB", "B: 24 GB");
    }

    private void Finished(InstallationResult result)
    {
        _session.SetupGet(s => s.Phase).Returns(result.IsSuccess ? InstallPhase.Completed : InstallPhase.Failed);
        _session.SetupGet(s => s.Result).Returns(result);
    }

    [Theory]
    [InlineData(true, "Update complete")]
    [InlineData(false, "Extension complete")]
    public async Task The_result_says_what_was_done(bool updateOnly, string heading)
    {
        Finished(InstallationResult.Success("ok", _repo));
        var run = updateOnly ? await UpdateRunAsync(updateOnly: true) : await UpdateRunAsync(false, new WorkloadChoice(Workload("A"), 16));

        var cut = Render<InstallStage>(p => p.Add(x => x.Run, run));

        cut.Find(".install-result h2").TextContent.Trim().Should().Be(heading);
        cut.Find(".panel-head h2").TextContent.Should().Be("Updating");
    }

    [Fact]
    public async Task A_run_with_something_that_failed_is_not_green()
    {
        Finished(InstallationResult.Success("ok", _repo));
        _session.SetupGet(s => s.ReportRows).Returns(
        [
            new SDK.Models.Installation.InstallReportEntry
            {
                PlannedOperation = "Updating ComfyUI",
                Category = SDK.Models.Installation.InstallReportCategory.Step,
                Outcome = SDK.Models.Installation.InstallReportOutcome.Failed,
                Comment = "ComfyUI code was not updated.",
                IsWarning = true,
            },
        ]);
        var run = await UpdateRunAsync(updateOnly: true);

        var heading = Render<InstallStage>(p => p.Add(x => x.Run, run)).Find(".install-result h2");

        heading.TextContent.Trim().Should().Be("Finished with problems");
        heading.ClassList.Should().Contain("result-bad");
    }

    [Fact]
    public async Task A_failed_run_says_update_failed()
    {
        Finished(InstallationResult.Failure("no"));

        var cut = Render<InstallStage>(p => p.Add(x => x.Run, UpdateRunAsync(updateOnly: true).Result));

        cut.Find(".install-result h2").TextContent.Trim().Should().Be("Update failed");
        await Task.CompletedTask;
    }

    [Fact]
    public void Model_rows_carry_the_workload_they_came_from()
    {
        var model = new ModelDownload { Name = "m", Url = "https://h/m.safetensors" };
        var a = Workload("A", tiers: string.Empty);
        a.ModelDownloads.Add(model);
        var selection = MergedSelections.ForPicks(WizardMode.ModelsOnly, [new WorkloadPick(a.Id, 0)], [a], null)!;
        var scanner = new Mock<IModelPresenceScanner>();
        scanner.Setup(s => s.Scan(It.IsAny<ModelScanRequest>())).Returns([]);
        var module = new ModelSelectionModule(scanner.Object, Mock.Of<IDiskSpaceEstimator>());
        module.InitializeAsync(selection).GetAwaiter().GetResult();

        module.Rows.Single().PackName.Should().Be("A");
    }

    [Fact]
    public async Task A_run_on_this_path_can_still_start_comfyui()
    {
        var run = await UpdateRunAsync(updateOnly: true);
        File.WriteAllText(Path.Combine(_repo, "run_nvidia.bat"), string.Empty);

        var targets = PostInstallTargets.From(run.Plan, InstallationResult.Success("ok", _repo));

        targets.Folder.Should().Be(_repo);
        targets.LauncherScript.Should().Be(Path.Combine(_repo, "run_nvidia.bat"));
    }
}
