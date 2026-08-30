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
        public Task InitializeAsync(WizardSelection s, CancellationToken ct = default) => Task.CompletedTask;
        public void Contribute(InstallationOptionsDraft d) { }
        public ModuleValidation Validate() =>
            valid ? ModuleValidation.Ok() : ModuleValidation.Error("not ready");
    }

    private static async Task<WizardRun> RunAsync(params IWizardModule[] modules)
    {
        var workload = new InstallationConfiguration { Name = "Fooocus" };
        workload.Repository.Type = RepositoryType.Fooocus;

        var plan = await new WizardModuleRegistry(modules)
            .BuildPlanAsync(new WizardSelection { Workload = workload });

        return new WizardRun(plan);
    }

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
}
