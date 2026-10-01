using DiffusionNexus.Installer.Core.Install;
using DiffusionNexus.Installer.Core.Updates;
using DiffusionNexus.Installer.Core.Wizard;
using DiffusionNexus.Installer.SDK.Models.Configuration;
using DiffusionNexus.Installer.SDK.Models.Installation;
using DiffusionNexus.Installer.SDK.Services;
using DiffusionNexus.Installer.Tests.Support;
using FluentAssertions;
using Moq;
using Xunit;
using InstallationOptions = DiffusionNexus.Installer.SDK.Services.InstallationOptions;

namespace DiffusionNexus.Installer.Tests.Install;

/// <summary>
/// Spec 7.2: an install names the catalog it used, in the log and in the result view, so a
/// support question can be answered from the report. The wizard captures it when it reads the
/// workload (PR #43 review: a channel switch can apply another catalog before Install is pressed);
/// a plan without one is read when the run starts.
/// </summary>
public class InstallSessionCatalogTests
{
    private const string Catalog = "Catalog v5 (Stable, 51e1684)";

    private static async Task<WizardPlan> PlanAsync(InstalledCatalogReading? captured = null)
    {
        var registry = new WizardModuleRegistry(() => []);
        var plan = await registry.BuildPlanAsync(new WizardSelection { Workload = new InstallationConfiguration { Name = "Fooocus" }, Catalog = captured });
        plan.Selection.TargetFolder = Path.Combine(Path.GetTempPath(), $"dn-session-catalog-{Guid.NewGuid():N}", "missing");
        return plan;
    }

    private static InstallReportEntry Row(string operation) => new()
    {
        PlannedOperation = operation,
        Category = InstallReportCategory.Step,
        Outcome = InstallReportOutcome.Success,
    };

    private static Mock<IInstallationOrchestrator> Orchestrator(Func<InstallationOptions, InstallationResult> run)
    {
        var orchestrator = new Mock<IInstallationOrchestrator>();
        orchestrator
            .Setup(o => o.InstallAsync(
                It.IsAny<InstallationConfiguration>(), It.IsAny<string>(), It.IsAny<InstallationOptions>(),
                It.IsAny<IProgress<InstallLogEntry>>(), It.IsAny<IProgress<InstallationProgress>>(),
                It.IsAny<IProgress<DownloadProgress>>(), It.IsAny<Func<CancellationToken>>(),
                It.IsAny<CancellationToken>()))
            .Returns((InstallationConfiguration _, string _, InstallationOptions options,
                      IProgress<InstallLogEntry>? _, IProgress<InstallationProgress>? _,
                      IProgress<DownloadProgress>? _, Func<CancellationToken>? _, CancellationToken _) =>
                Task.FromResult(run(options)));
        return orchestrator;
    }

    [Fact]
    public async Task The_catalog_is_the_first_log_line_and_report_row_before_any_step_runs()
    {
        InstallSession? session = null;
        string? firstLineMidRun = null;
        string? firstRowMidRun = null;
        var orchestrator = Orchestrator(options =>
        {
            firstLineMidRun = session!.LogLines.FirstOrDefault()?.Message;
            firstRowMidRun = session.ReportRows.FirstOrDefault()?.PlannedOperation;
            options.OnReportRow!(Row("Setting up Git"));
            return InstallationResult.Success("done");
        });
        session = new InstallSession(orchestrator.Object, catalog: new FakeCatalogProvenance(() => Catalog));

        await session.StartAsync(await PlanAsync());

        firstLineMidRun.Should().Be(Catalog);
        firstRowMidRun.Should().Be(Catalog);
        session.ReportRows.Select(r => r.PlannedOperation).Should().Equal(Catalog, "Setting up Git");
        session.ReportRows[0].Outcome.Should().Be(InstallReportOutcome.Success);
    }

    // PR #43 review: the wizard read workload X from Preview v6, then a switch applied Stable v5
    // before Install. The plan installs v6's X, so the report names v6, not what is installed now.
    [Fact]
    public async Task The_catalog_the_wizard_captured_wins_over_what_is_installed_at_start()
    {
        var orchestrator = Orchestrator(_ => InstallationResult.Success("done"));
        var provenance = new FakeCatalogProvenance(() => "Catalog v5 (Stable, 51e1684)");
        var session = new InstallSession(orchestrator.Object, catalog: provenance);

        await session.StartAsync(await PlanAsync(new InstalledCatalogReading("Catalog v6 (Preview, 2df647e)", false)));

        session.LogLines[0].Message.Should().Be("Catalog v6 (Preview, 2df647e)");
        session.ReportRows[0].PlannedOperation.Should().Be("Catalog v6 (Preview, 2df647e)");
        provenance.Reads.Should().Be(0, "the captured line is the answer; reading again could only contradict it");
    }

    // The finished report replaces the streamed rows (it carries the "not run" rows of an aborted
    // run). It comes from the SDK, which knows nothing of the catalog row, so the row is kept.
    [Fact]
    public async Task The_catalog_row_survives_the_finished_report()
    {
        var orchestrator = Orchestrator(_ => InstallationResult.Failure("failed", [Row("Setting up Git"), Row("Installing requirements")]));
        var session = new InstallSession(orchestrator.Object, catalog: new FakeCatalogProvenance(() => Catalog));

        await session.StartAsync(await PlanAsync());

        session.ReportRows.Select(r => r.PlannedOperation).Should().Equal(Catalog, "Setting up Git", "Installing requirements");
    }

    [Fact]
    public async Task A_plan_without_a_captured_catalog_is_read_when_each_install_starts()
    {
        var reads = 0;
        var orchestrator = Orchestrator(_ => InstallationResult.Success("done"));
        var session = new InstallSession(orchestrator.Object, catalog: new FakeCatalogProvenance(() => $"Catalog v{++reads} (Stable)"));
        reads.Should().Be(0);

        await session.StartAsync(await PlanAsync());
        await session.StartAsync(await PlanAsync());

        reads.Should().Be(2);
        session.ReportRows[0].PlannedOperation.Should().Be("Catalog v2 (Stable)");
        session.LogLines.Count(l => l.Message.StartsWith("Catalog", StringComparison.Ordinal)).Should().Be(1, "each run starts its own log");
    }

    // PR #43 review round 2: a reading that failed when the wizard opened (the switch's apply was
    // writing the state, an AV scan held it) was final, although the file reads fine at Install.
    [Fact]
    public async Task A_reading_that_failed_when_the_wizard_opened_is_taken_again_at_install()
    {
        var orchestrator = Orchestrator(_ => InstallationResult.Success("done"));
        var provenance = new FakeCatalogProvenance(() => Catalog);
        var session = new InstallSession(orchestrator.Object, catalog: provenance);

        await session.StartAsync(await PlanAsync(new InstalledCatalogReading("Catalog: could not be read: locked", true)));

        provenance.Reads.Should().Be(1);
        session.ReportRows[0].PlannedOperation.Should().Be(Catalog);
        session.ReportRows[0].IsWarning.Should().BeFalse();
    }

    [Fact]
    public async Task A_catalog_that_cannot_be_read_is_a_warning_row_never_a_stop()
    {
        var ran = false;
        var orchestrator = Orchestrator(_ => { ran = true; return InstallationResult.Success("done"); });
        var session = new InstallSession(orchestrator.Object, catalog: new FakeCatalogProvenance(() => throw new IOException("catalog-state.json is locked")));

        await session.StartAsync(await PlanAsync());

        ran.Should().BeTrue();
        session.Phase.Should().Be(InstallPhase.Completed);
        session.LogLines[0].Message.Should().Be("Catalog: could not be read: catalog-state.json is locked");
        session.ReportRows[0].PlannedOperation.Should().Be("Catalog: could not be read: catalog-state.json is locked");
        session.ReportRows[0].IsWarning.Should().BeTrue();
    }
}
