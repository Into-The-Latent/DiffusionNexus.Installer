using DiffusionNexus.Installer.Core.Install;
using DiffusionNexus.Installer.Core.Modules;
using DiffusionNexus.Installer.Core.Wizard;
using DiffusionNexus.Installer.SDK.Models.Configuration;
using DiffusionNexus.Installer.SDK.Services;
using DiffusionNexus.Installer.SDK.Services.Settings;
using FluentAssertions;
using Moq;
using Xunit;
using SdkLogLevel = DiffusionNexus.Installer.SDK.Models.Enums.LogLevel;
// The SDK has two InstallationOptions types (Models.Installation and Services); this file uses
// the Services one everywhere, so only UserSettings is aliased in rather than the whole namespace.
using UserSettings = DiffusionNexus.Installer.SDK.Models.Installation.UserSettings;

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
    public async Task Plan_is_null_until_a_run_starts_then_reflects_it()
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
        session.Plan.Should().BeNull();

        var plan = await PlanAsync();
        await session.StartAsync(plan);

        session.Plan.Should().BeSameAs(plan);
    }

    [Fact]
    public async Task The_orchestrator_receives_the_folder_the_module_set_even_without_a_prior_ToOptions_call()
    {
        // Regression test for an argument-evaluation-order bug: plan.Selection.TargetFolder was
        // passed as an argument alongside plan.ToOptions(), and C# evaluates arguments left to
        // right -- so the folder was read before ToOptions() ran Contribute, which is what writes
        // the module's answer into the selection. It only worked before because some other render
        // path (ConfirmStage) happened to call ToOptions() first, as a side effect.
        var settings = new Mock<IUserSettingsRepository>();
        settings.Setup(s => s.GetOrCreateForCurrentUserAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new UserSettings());
        var folderModule = new InstallFolderModule(settings.Object);

        var workload = new InstallationConfiguration { Name = "Fooocus" };
        workload.Repository.Type = RepositoryType.Fooocus;

        var registry = new WizardModuleRegistry([folderModule]);
        var plan = await registry.BuildPlanAsync(new WizardSelection { Workload = workload });

        // The module has an answer, but nothing has called ToOptions() yet.
        folderModule.TargetFolder = @"C:\Installs\Fooocus";
        plan.Selection.TargetFolder.Should().BeEmpty("Contribute has not run yet");

        string? receivedTargetDirectory = null;
        var orchestrator = new Mock<IInstallationOrchestrator>();
        orchestrator
            .Setup(o => o.InstallAsync(
                It.IsAny<InstallationConfiguration>(), It.IsAny<string>(), It.IsAny<InstallationOptions>(),
                It.IsAny<IProgress<InstallLogEntry>>(), It.IsAny<IProgress<InstallationProgress>>(),
                It.IsAny<IProgress<DownloadProgress>>(), It.IsAny<Func<CancellationToken>>(),
                It.IsAny<CancellationToken>()))
            .Callback<InstallationConfiguration, string, InstallationOptions, IProgress<InstallLogEntry>,
                      IProgress<InstallationProgress>, IProgress<DownloadProgress>, Func<CancellationToken>, CancellationToken>(
                (_, targetDirectory, _, _, _, _, _, _) => receivedTargetDirectory = targetDirectory)
            .ReturnsAsync(InstallationResult.Success("done"));

        var session = new InstallSession(orchestrator.Object);

        await session.StartAsync(plan);

        receivedTargetDirectory.Should().Be(@"C:\Installs\Fooocus");
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
    public async Task A_cancelled_result_from_the_orchestrator_lands_as_the_cancelled_phase()
    {
        // Renamed from Cancellation_lands_as_the_cancelled_phase: this only pins the result-mapping
        // switch in StartAsync's catch/completion logic. It never calls session.Cancel() and proves
        // nothing about that path -- see Cancel_cancels_the_token_the_orchestrator_was_handed below.
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
    public async Task Cancel_cancels_the_token_the_orchestrator_was_handed()
    {
        // _cts used to be assigned outside the lock that flips Phase to Running, so a Cancel()
        // landing in that window read a null field and was silently dropped -- the user pressed
        // Cancel and nothing happened. This drives an install partway in, captures the token the
        // orchestrator actually received, and proves Cancel() reaches that exact instance.
        var gate = new TaskCompletionSource();
        CancellationToken? capturedToken = null;
        var orchestrator = new Mock<IInstallationOrchestrator>();
        orchestrator
            .Setup(o => o.InstallAsync(
                It.IsAny<InstallationConfiguration>(), It.IsAny<string>(), It.IsAny<InstallationOptions>(),
                It.IsAny<IProgress<InstallLogEntry>>(), It.IsAny<IProgress<InstallationProgress>>(),
                It.IsAny<IProgress<DownloadProgress>>(), It.IsAny<Func<CancellationToken>>(),
                It.IsAny<CancellationToken>()))
            .Returns(async (InstallationConfiguration _, string _, InstallationOptions _,
                            IProgress<InstallLogEntry>? _, IProgress<InstallationProgress>? _,
                            IProgress<DownloadProgress>? _, Func<CancellationToken>? _, CancellationToken token) =>
            {
                capturedToken = token;
                await gate.Task;
                return InstallationResult.Success("done");
            });

        var session = new InstallSession(orchestrator.Object);

        // Synchronous up to the orchestrator's own first await (matching the pattern the
        // second-start test above relies on), so capturedToken is already set once this returns.
        var run = session.StartAsync(await PlanAsync());

        session.Cancel();

        capturedToken.Should().NotBeNull();
        capturedToken!.Value.IsCancellationRequested.Should().BeTrue();

        gate.SetResult();
        await run;
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

    [Fact]
    public async Task Skipping_cancels_the_current_download_token_and_hands_out_a_fresh_one()
    {
        // Sequential contract test, not a race test: it pins that a skip both cancels the token the
        // orchestrator is currently holding AND leaves a fresh, uncancelled one in place for the next
        // file. It would fail if either half of the swap were dropped. The race itself is guarded by
        // the lock in SkipCurrentDownload/GetSkipDownloadToken, not by this test.
        Func<CancellationToken>? provider = null;
        var orchestrator = new Mock<IInstallationOrchestrator>();
        orchestrator
            .Setup(o => o.InstallAsync(
                It.IsAny<InstallationConfiguration>(), It.IsAny<string>(), It.IsAny<InstallationOptions>(),
                It.IsAny<IProgress<InstallLogEntry>>(), It.IsAny<IProgress<InstallationProgress>>(),
                It.IsAny<IProgress<DownloadProgress>>(), It.IsAny<Func<CancellationToken>>(),
                It.IsAny<CancellationToken>()))
            .Returns((InstallationConfiguration _, string _, InstallationOptions _,
                      IProgress<InstallLogEntry>? _, IProgress<InstallationProgress>? _,
                      IProgress<DownloadProgress>? _, Func<CancellationToken>? skip, CancellationToken _) =>
            {
                provider = skip;
                return Task.FromResult(InstallationResult.Success("done"));
            });

        using var session = new InstallSession(orchestrator.Object);
        await session.StartAsync(await PlanAsync());

        provider.Should().NotBeNull();

        // The token the orchestrator would be carrying for the file in flight.
        var tokenBeforeSkip = provider!();
        tokenBeforeSkip.IsCancellationRequested.Should().BeFalse();

        session.SkipCurrentDownload();

        tokenBeforeSkip.IsCancellationRequested.Should().BeTrue("the in-flight download is the one being skipped");
        provider!().IsCancellationRequested.Should().BeFalse("the next file must start with a live token");
    }
}
