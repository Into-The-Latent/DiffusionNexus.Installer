using DiffusionNexus.Installer.Core.Install;
using DiffusionNexus.Installer.Core.Wizard;
using DiffusionNexus.Installer.SDK.Models.Configuration;
using DiffusionNexus.Installer.SDK.Services;
using FluentAssertions;
using Moq;
using Xunit;
using SdkLogLevel = DiffusionNexus.Installer.SDK.Models.Enums.LogLevel;

namespace DiffusionNexus.Installer.Tests.Install;

public class InstallSessionTests
{
    private static async Task<WizardPlan> PlanAsync()
    {
        var workload = new InstallationConfiguration { Name = "Fooocus" };
        workload.Repository.Type = RepositoryType.Fooocus;

        var registry = new WizardModuleRegistry([]);
        var plan = await registry.BuildPlanAsync(new WizardSelection { Workload = workload });
        plan.Selection.TargetFolder = @"C:\Installs\Fooocus";
        return plan;
    }

    [Fact]
    public async Task A_successful_run_ends_completed_and_keeps_the_report()
    {
        var orchestrator = new Mock<IInstallationOrchestrator>();
        orchestrator
            .Setup(o => o.InstallAsync(
                It.IsAny<InstallationConfiguration>(), It.IsAny<string>(), It.IsAny<InstallationOptions>(),
                It.IsAny<IProgress<InstallLogEntry>>(), It.IsAny<IProgress<InstallationProgress>>(),
                It.IsAny<IProgress<DownloadProgress>>(), It.IsAny<Func<CancellationToken>>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(InstallationResult.Success("done", @"C:\Installs\Fooocus"));

        var session = new InstallSession(orchestrator.Object);

        await session.StartAsync(await PlanAsync());

        session.Phase.Should().Be(InstallPhase.Completed);
        session.Result!.IsSuccess.Should().BeTrue();
    }

    [Fact]
    public async Task A_second_start_while_running_is_refused()
    {
        var gate = new TaskCompletionSource();
        var orchestrator = new Mock<IInstallationOrchestrator>();
        orchestrator
            .Setup(o => o.InstallAsync(
                It.IsAny<InstallationConfiguration>(), It.IsAny<string>(), It.IsAny<InstallationOptions>(),
                It.IsAny<IProgress<InstallLogEntry>>(), It.IsAny<IProgress<InstallationProgress>>(),
                It.IsAny<IProgress<DownloadProgress>>(), It.IsAny<Func<CancellationToken>>(),
                It.IsAny<CancellationToken>()))
            .Returns(async () => { await gate.Task; return InstallationResult.Success("done"); });

        var session = new InstallSession(orchestrator.Object);
        var plan = await PlanAsync();

        var first = session.StartAsync(plan);

        var second = async () => await session.StartAsync(plan);
        await second.Should().ThrowAsync<InvalidOperationException>();

        gate.SetResult();
        await first;
    }

    [Fact]
    public async Task Log_lines_are_captured_and_bounded()
    {
        var orchestrator = new Mock<IInstallationOrchestrator>();
        orchestrator
            .Setup(o => o.InstallAsync(
                It.IsAny<InstallationConfiguration>(), It.IsAny<string>(), It.IsAny<InstallationOptions>(),
                It.IsAny<IProgress<InstallLogEntry>>(), It.IsAny<IProgress<InstallationProgress>>(),
                It.IsAny<IProgress<DownloadProgress>>(), It.IsAny<Func<CancellationToken>>(),
                It.IsAny<CancellationToken>()))
            .Returns((InstallationConfiguration _, string _, InstallationOptions _,
                      IProgress<InstallLogEntry>? log, IProgress<InstallationProgress>? _,
                      IProgress<DownloadProgress>? _, Func<CancellationToken>? _, CancellationToken _) =>
            {
                for (var i = 0; i < InstallSession.MaxLogLines + 50; i++)
                    log!.Report(new InstallLogEntry { Message = $"line {i}", Level = SdkLogLevel.Info });
                return Task.FromResult(InstallationResult.Success("done"));
            });

        var session = new InstallSession(orchestrator.Object);

        await session.StartAsync(await PlanAsync());

        session.LogLines.Count.Should().Be(InstallSession.MaxLogLines);
        session.LogLines.Last().Message.Should().Be($"line {InstallSession.MaxLogLines + 49}");
    }

    [Fact]
    public async Task Log_lines_are_coalesced_rather_than_notified_one_by_one()
    {
        var orchestrator = new Mock<IInstallationOrchestrator>();
        orchestrator
            .Setup(o => o.InstallAsync(
                It.IsAny<InstallationConfiguration>(), It.IsAny<string>(), It.IsAny<InstallationOptions>(),
                It.IsAny<IProgress<InstallLogEntry>>(), It.IsAny<IProgress<InstallationProgress>>(),
                It.IsAny<IProgress<DownloadProgress>>(), It.IsAny<Func<CancellationToken>>(),
                It.IsAny<CancellationToken>()))
            .Returns((InstallationConfiguration _, string _, InstallationOptions _,
                      IProgress<InstallLogEntry>? log, IProgress<InstallationProgress>? _,
                      IProgress<DownloadProgress>? _, Func<CancellationToken>? _, CancellationToken _) =>
            {
                for (var i = 0; i < 500; i++)
                    log!.Report(new InstallLogEntry { Message = $"line {i}", Level = SdkLogLevel.Info });
                return Task.FromResult(InstallationResult.Success("done"));
            });

        // A flush interval long enough that no tick can fire during the run isolates the
        // coalescing from timing: only the two phase transitions may notify.
        using var session = new InstallSession(orchestrator.Object, TimeSpan.FromMinutes(10));

        var notifications = 0;
        session.Changed += () => notifications++;

        await session.StartAsync(await PlanAsync());

        notifications.Should().Be(2, "only the start and the terminal transition bypass coalescing");
        session.LogLines.Should().HaveCount(500, "every line is still captured");
    }

    [Fact]
    public async Task Cancellation_lands_as_the_cancelled_phase()
    {
        var orchestrator = new Mock<IInstallationOrchestrator>();
        orchestrator
            .Setup(o => o.InstallAsync(
                It.IsAny<InstallationConfiguration>(), It.IsAny<string>(), It.IsAny<InstallationOptions>(),
                It.IsAny<IProgress<InstallLogEntry>>(), It.IsAny<IProgress<InstallationProgress>>(),
                It.IsAny<IProgress<DownloadProgress>>(), It.IsAny<Func<CancellationToken>>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(InstallationResult.Cancelled("cancelled by user"));

        var session = new InstallSession(orchestrator.Object);

        await session.StartAsync(await PlanAsync());

        session.Phase.Should().Be(InstallPhase.Cancelled);
    }

    [Fact]
    public async Task An_unexpected_exception_becomes_a_failed_result_not_a_throw()
    {
        var orchestrator = new Mock<IInstallationOrchestrator>();
        orchestrator
            .Setup(o => o.InstallAsync(
                It.IsAny<InstallationConfiguration>(), It.IsAny<string>(), It.IsAny<InstallationOptions>(),
                It.IsAny<IProgress<InstallLogEntry>>(), It.IsAny<IProgress<InstallationProgress>>(),
                It.IsAny<IProgress<DownloadProgress>>(), It.IsAny<Func<CancellationToken>>(),
                It.IsAny<CancellationToken>()))
            .ThrowsAsync(new IOException("disk full"));

        var session = new InstallSession(orchestrator.Object);

        await session.StartAsync(await PlanAsync());

        session.Phase.Should().Be(InstallPhase.Failed);
        session.Result!.Message.Should().Contain("disk full");
    }

    [Fact]
    public async Task State_outlives_a_subscriber_that_goes_away()
    {
        var orchestrator = new Mock<IInstallationOrchestrator>();
        orchestrator
            .Setup(o => o.InstallAsync(
                It.IsAny<InstallationConfiguration>(), It.IsAny<string>(), It.IsAny<InstallationOptions>(),
                It.IsAny<IProgress<InstallLogEntry>>(), It.IsAny<IProgress<InstallationProgress>>(),
                It.IsAny<IProgress<DownloadProgress>>(), It.IsAny<Func<CancellationToken>>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(InstallationResult.Success("done"));

        var session = new InstallSession(orchestrator.Object);

        var notifications = 0;
        void Handler() => notifications++;
        session.Changed += Handler;
        session.Changed -= Handler;   // the circuit dropped

        await session.StartAsync(await PlanAsync());

        session.Phase.Should().Be(InstallPhase.Completed);
        notifications.Should().Be(0);
    }
}
