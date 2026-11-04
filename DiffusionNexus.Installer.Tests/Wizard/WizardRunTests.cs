using DiffusionNexus.Installer.Core.Wizard;
using DiffusionNexus.Installer.SDK.Models.Configuration;
using FluentAssertions;
using Xunit;

namespace DiffusionNexus.Installer.Tests.Wizard;

public class WizardRunTests
{
    private sealed class GateModule(WizardStage stage, bool valid) : IWizardModule
    {
        public string Id => $"gate-{stage}";
        public WizardStage Stage => stage;
        public int Order => 0;
        public WorkloadCapability Satisfies => WorkloadCapability.None;
        public bool AppliesTo(WizardSelection s) => true;
        public bool UsedIn(WizardMode mode) => true;
        public Task InitializeAsync(WizardSelection s, CancellationToken ct = default) => Task.CompletedTask;
        public void Contribute(InstallationOptionsDraft d) { }
        public ModuleValidation Validate() =>
            valid ? ModuleValidation.Ok() : ModuleValidation.Error("not ready");
    }

    private sealed class ToggleModule(WizardStage stage) : IWizardModule
    {
        public bool Applies { get; set; }
        public string Id => $"toggle-{stage}";
        public WizardStage Stage => stage;
        public int Order => 0;
        public WorkloadCapability Satisfies => WorkloadCapability.None;
        public bool AppliesTo(WizardSelection s) => Applies;
        public bool UsedIn(WizardMode mode) => true;
        public Task InitializeAsync(WizardSelection s, CancellationToken ct = default) => Task.CompletedTask;
        public void Contribute(InstallationOptionsDraft d) { }
        public ModuleValidation Validate() => ModuleValidation.Ok();
    }

    private static async Task<WizardRun> UpdateRunAsync(params IWizardModule[] modules)
    {
        var workload = new InstallationConfiguration { Name = "ComfyUI" };
        return new WizardRun(await new WizardModuleRegistry(() => modules)
            .BuildPlanAsync(new WizardSelection { Workload = workload, Mode = WizardMode.UpdateExisting }));
    }

    [Fact]
    public async Task A_stage_that_appears_behind_the_current_one_is_shown_and_not_skipped()
    {
        // The Visual C++ page announces a UAC prompt. Skipping it would leave that prompt unexplained,
        // and the module would still contribute its default.
        var system = new ToggleModule(WizardStage.System);
        var run = await UpdateRunAsync(new GateModule(WizardStage.Location, valid: true), system, new GateModule(WizardStage.Confirm, valid: true));
        run.TryNext().Should().BeTrue();
        run.CurrentStage.Should().Be(WizardStage.Confirm);

        system.Applies = true;
        run.TryNext().Should().BeTrue();

        run.CurrentStage.Should().Be(WizardStage.System);
        run.TryNext().Should().BeTrue();
        run.CurrentStage.Should().Be(WizardStage.Confirm);
        run.TryNext().Should().BeTrue();
        run.CurrentStage.Should().Be(WizardStage.Install);
    }

    [Fact]
    public async Task A_stage_that_was_there_all_along_does_not_send_the_run_back()
    {
        var system = new ToggleModule(WizardStage.System) { Applies = true };
        var run = await UpdateRunAsync(system, new GateModule(WizardStage.Confirm, valid: true));
        run.TryNext().Should().BeTrue();
        run.CurrentStage.Should().Be(WizardStage.Confirm);

        run.TryNext().Should().BeTrue();

        run.CurrentStage.Should().Be(WizardStage.Install);
    }

    private static async Task<WizardPlan> BuildPlanAsync(params IWizardModule[] modules)
    {
        var workload = new InstallationConfiguration { Name = "Fooocus" };
        workload.Repository.Type = RepositoryType.Fooocus;

        return await new WizardModuleRegistry(() => modules)
            .BuildPlanAsync(new WizardSelection { Workload = workload });
    }

    private static async Task<WizardRun> RunAsync(params IWizardModule[] modules) =>
        new(await BuildPlanAsync(modules));

    [Fact]
    public async Task Starts_on_the_first_populated_stage()
    {
        var run = await RunAsync(new GateModule(WizardStage.System, valid: true));

        run.CurrentStage.Should().Be(WizardStage.System);
    }

    [Fact]
    public async Task Advances_through_the_planned_stages_only()
    {
        var run = await RunAsync(new GateModule(WizardStage.Location, valid: true));

        run.CurrentStage.Should().Be(WizardStage.Location);
        run.TryNext().Should().BeTrue();
        run.CurrentStage.Should().Be(WizardStage.Confirm);
        run.TryNext().Should().BeTrue();
        run.CurrentStage.Should().Be(WizardStage.Install);
    }

    [Fact]
    public async Task Cannot_advance_past_an_invalid_stage()
    {
        var run = await RunAsync(new GateModule(WizardStage.Location, valid: false));

        run.CanGoNext.Should().BeFalse();
        run.TryNext().Should().BeFalse();
        run.CurrentStage.Should().Be(WizardStage.Location);
        run.ValidationErrors.Should().ContainSingle().Which.Should().Be("not ready");
    }

    [Fact]
    public async Task Cannot_go_back_from_the_first_stage()
    {
        var run = await RunAsync(new GateModule(WizardStage.Location, valid: true));

        run.CanGoBack.Should().BeFalse();
    }

    [Fact]
    public async Task Cannot_go_back_out_of_the_install_stage()
    {
        var run = await RunAsync(new GateModule(WizardStage.Location, valid: true));
        run.TryNext();
        run.TryNext();

        run.CurrentStage.Should().Be(WizardStage.Install);
        run.CanGoBack.Should().BeFalse();
    }

    [Fact]
    public async Task Back_returns_to_the_previous_planned_stage()
    {
        var run = await RunAsync(new GateModule(WizardStage.Location, valid: true));
        run.TryNext();

        run.Back();

        run.CurrentStage.Should().Be(WizardStage.Location);
    }

    [Fact]
    public async Task Opens_on_the_requested_stage_when_it_is_in_the_plan()
    {
        var plan = await BuildPlanAsync(new GateModule(WizardStage.Location, valid: true));

        var run = new WizardRun(plan, WizardStage.Install);

        run.CurrentStage.Should().Be(WizardStage.Install);
    }

    [Fact]
    public async Task Falls_back_to_the_first_stage_when_the_requested_stage_is_not_in_the_plan()
    {
        var plan = await BuildPlanAsync(new GateModule(WizardStage.Location, valid: true));

        var run = new WizardRun(plan, WizardStage.System);

        run.CurrentStage.Should().Be(WizardStage.Location);
    }
}
