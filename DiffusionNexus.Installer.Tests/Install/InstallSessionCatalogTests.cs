using DiffusionNexus.Installer.Core.Install;
using DiffusionNexus.Installer.Core.Wizard;
using DiffusionNexus.Installer.SDK.Models.Configuration;
using DiffusionNexus.Installer.SDK.Models.Installation;
using DiffusionNexus.Installer.SDK.Services;
using FluentAssertions;
using Moq;
using Xunit;
using InstallationOptions = DiffusionNexus.Installer.SDK.Services.InstallationOptions;

namespace DiffusionNexus.Installer.Tests.Install;

/// <summary>
/// Spec 7.2: an install names the catalog it used, in the log and in the result view, as read
/// when it starts -- so a support question can be answered from the report.
/// </summary>
public class InstallSessionCatalogTests
{
    private const string Catalog = "Catalog v5 (Stable, 51e1684)";

    private static async Task<WizardPlan> PlanAsync()
    {
        var registry = new WizardModuleRegistry(() => []);
        var plan = await registry.BuildPlanAsync(new WizardSelection { Workload = new InstallationConfiguration { Name = "Fooocus" } });
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
        session = new InstallSession(orchestrator.Object, describeCatalog: () => Catalog);

        await session.StartAsync(await PlanAsync());

        firstLineMidRun.Should().Be(Catalog);
        firstRowMidRun.Should().Be(Catalog);
        session.ReportRows.Select(r => r.PlannedOperation).Should().Equal(Catalog, "Setting up Git");
        session.ReportRows[0].Outcome.Should().Be(InstallReportOutcome.Success);
    }

    // The finished report replaces the streamed rows (it carries the "not run" rows of an aborted
    // run). It comes from the SDK, which knows nothing of the catalog row, so the row is kept.
    [Fact]
    public async Task The_catalog_row_survives_the_finished_report()
    {
        var orchestrator = Orchestrator(_ => InstallationResult.Failure("failed", [Row("Setting up Git"), Row("Installing requirements")]));
        var session = new InstallSession(orchestrator.Object, describeCatalog: () => Catalog);

        await session.StartAsync(await PlanAsync());

        session.ReportRows.Select(r => r.PlannedOperation).Should().Equal(Catalog, "Setting up Git", "Installing requirements");
    }

    [Fact]
    public async Task The_catalog_is_read_when_each_install_starts()
    {
        var reads = 0;
        var orchestrator = Orchestrator(_ => InstallationResult.Success("done"));
        var session = new InstallSession(orchestrator.Object, describeCatalog: () => $"Catalog v{++reads} (Stable)");
        reads.Should().Be(0);

        await session.StartAsync(await PlanAsync());
        await session.StartAsync(await PlanAsync());

        reads.Should().Be(2);
        session.ReportRows[0].PlannedOperation.Should().Be("Catalog v2 (Stable)");
        session.LogLines.Count(l => l.Message.StartsWith("Catalog", StringComparison.Ordinal)).Should().Be(1, "each run starts its own log");
    }

    [Fact]
    public async Task A_catalog_that_cannot_be_described_never_stops_the_install()
    {
        var ran = false;
        var orchestrator = Orchestrator(_ => { ran = true; return InstallationResult.Success("done"); });
        var session = new InstallSession(orchestrator.Object, describeCatalog: () => throw new IOException("catalog-state.json is locked"));

        await session.StartAsync(await PlanAsync());

        ran.Should().BeTrue();
        session.Phase.Should().Be(InstallPhase.Completed);
        session.LogLines[0].Message.Should().Be("Catalog: could not be read: catalog-state.json is locked");
        session.ReportRows[0].PlannedOperation.Should().Be("Catalog: could not be read: catalog-state.json is locked");
        session.ReportRows[0].IsWarning.Should().BeTrue();
    }
}
