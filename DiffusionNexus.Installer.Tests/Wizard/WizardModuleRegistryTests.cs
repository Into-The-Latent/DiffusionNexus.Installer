using DiffusionNexus.Installer.Core.Wizard;
using DiffusionNexus.Installer.SDK.Models.Configuration;
using DiffusionNexus.Installer.SDK.Models.Entities;
using FluentAssertions;
using Xunit;

namespace DiffusionNexus.Installer.Tests.Wizard;

public class WizardModuleRegistryTests
{
    private sealed class StubModule(
        string id, WizardStage stage, int order, WorkloadCapability satisfies, bool applies,
        List<string>? recordInitializationInto = null) : IWizardModule
    {
        public string Id => id;
        public WizardStage Stage => stage;
        public int Order => order;
        public WorkloadCapability Satisfies => satisfies;
        public int InitializeCount { get; private set; }
        public bool Initialized => InitializeCount > 0;

        // Depends on Initialized deliberately: if BuildPlanAsync ever filtered before it
        // initialized, this module would report "does not apply" and vanish from the plan --
        // which is exactly what would happen to GpuPreflightModule and its hardware probe.
        public bool AppliesTo(WizardSelection selection) => Initialized && applies;

        public Task InitializeAsync(WizardSelection selection, CancellationToken ct = default)
        {
            InitializeCount++;
            recordInitializationInto?.Add(id);
            return Task.CompletedTask;
        }
        public void Contribute(InstallationOptionsDraft draft) { }
        public ModuleValidation Validate() => ModuleValidation.Ok();
    }

    private static WizardSelection Selection(InstallationConfiguration? workload = null) =>
        new() { Workload = workload ?? new InstallationConfiguration { Name = "x" } };

    [Fact]
    public async Task Only_applicable_modules_land_in_the_plan()
    {
        var yes = new StubModule("yes", WizardStage.Location, 0, WorkloadCapability.None, applies: true);
        var no = new StubModule("no", WizardStage.Location, 1, WorkloadCapability.None, applies: false);
        var registry = new WizardModuleRegistry([yes, no]);

        var plan = await registry.BuildPlanAsync(Selection());

        plan.Modules(WizardStage.Location).Should().ContainSingle().Which.Id.Should().Be("yes");
    }

    [Fact]
    public async Task Empty_stages_are_skipped()
    {
        var location = new StubModule("loc", WizardStage.Location, 0, WorkloadCapability.None, applies: true);
        var registry = new WizardModuleRegistry([location]);

        var plan = await registry.BuildPlanAsync(Selection());

        plan.Stages.Should().Equal(WizardStage.Location, WizardStage.Confirm, WizardStage.Install);
        plan.Stages.Should().NotContain(WizardStage.Content);
    }

    [Fact]
    public async Task Modules_render_in_order_within_a_stage()
    {
        var second = new StubModule("second", WizardStage.Location, 10, WorkloadCapability.None, applies: true);
        var first = new StubModule("first", WizardStage.Location, 0, WorkloadCapability.None, applies: true);
        var registry = new WizardModuleRegistry([second, first]);

        var plan = await registry.BuildPlanAsync(Selection());

        plan.Modules(WizardStage.Location).Select(m => m.Id).Should().Equal("first", "second");
    }

    [Fact]
    public async Task Modules_initialize_in_stage_then_order_sequence_not_registration_order()
    {
        // Registered deliberately out of Stage/Order sequence. Contribute already runs in
        // Stage-then-Order via WizardPlan.ToOptions, and InitializeAsync must match it: a
        // downstream module can depend on an upstream module's InitializeAsync-produced answer
        // (the spec's VRAM -> ModelSelection example), which only holds if initialization runs in
        // that same sequence rather than whatever order the modules happened to be registered in.
        var sequence = new List<string>();
        var system = new StubModule("system", WizardStage.System, 0, WorkloadCapability.None, applies: true, sequence);
        var locationLate = new StubModule("location-late", WizardStage.Location, 10, WorkloadCapability.None, applies: true, sequence);
        var locationEarly = new StubModule("location-early", WizardStage.Location, 0, WorkloadCapability.None, applies: true, sequence);

        var registry = new WizardModuleRegistry([system, locationLate, locationEarly]);

        await registry.BuildPlanAsync(Selection());

        sequence.Should().Equal("location-early", "location-late", "system");
    }

    [Fact]
    public async Task Applicability_is_read_only_after_every_module_is_initialized()
    {
        var module = new StubModule("m", WizardStage.System, 0, WorkloadCapability.None, applies: true);
        var registry = new WizardModuleRegistry([module]);

        var plan = await registry.BuildPlanAsync(Selection());

        module.Initialized.Should().BeTrue();
        plan.Modules(WizardStage.System).Should().ContainSingle().Which.Id.Should().Be("m");
    }

    [Fact]
    public void A_workload_needing_an_unregistered_capability_is_not_installable()
    {
        var registry = new WizardModuleRegistry(
            [new StubModule("folders", WizardStage.Location, 0, WorkloadCapability.ComfyFolders, applies: true)]);

        var heavy = new InstallationConfiguration();
        heavy.Repository.Type = RepositoryType.ComfyUI;
        heavy.ModelDownloads.Add(new ModelDownload());

        registry.IsInstallable(heavy).Should().BeFalse();
    }

    [Fact]
    public void A_workload_whose_capabilities_are_all_covered_is_installable()
    {
        var registry = new WizardModuleRegistry(
            [new StubModule("folders", WizardStage.Location, 0, WorkloadCapability.ComfyFolders, applies: true)]);

        var blank = new InstallationConfiguration();
        blank.Repository.Type = RepositoryType.ComfyUI;

        registry.IsInstallable(blank).Should().BeTrue();
    }

    [Fact]
    public void A_thin_workload_is_installable_with_no_capability_modules_at_all()
    {
        var registry = new WizardModuleRegistry([]);

        var fooocus = new InstallationConfiguration();
        fooocus.Repository.Type = RepositoryType.Fooocus;

        registry.IsInstallable(fooocus).Should().BeTrue();
    }

    [Fact]
    public async Task Building_a_second_plan_reinitializes_the_same_module_instances()
    {
        // Pins the one-plan-at-a-time contract documented on WizardModuleRegistry: the two plans
        // share module instances, so a future flow that kept both alive would silently share state.
        var module = new StubModule("m", WizardStage.Location, 0, WorkloadCapability.None, applies: true);
        var registry = new WizardModuleRegistry([module]);

        var first = await registry.BuildPlanAsync(Selection());
        var second = await registry.BuildPlanAsync(Selection());

        module.InitializeCount.Should().Be(2);
        first.AllModules.Single().Should().BeSameAs(second.AllModules.Single());
    }
}
