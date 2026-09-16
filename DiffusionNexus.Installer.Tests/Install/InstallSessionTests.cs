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
using InstallReportEntry = DiffusionNexus.Installer.SDK.Models.Installation.InstallReportEntry;

namespace DiffusionNexus.Installer.Tests.Install;

public class InstallSessionTests
{
    private static async Task<WizardPlan> PlanAsync()
    {
        var workload = new InstallationConfiguration { Name = "Fooocus" };
        workload.Repository.Type = RepositoryType.Fooocus;

        var registry = new WizardModuleRegistry(() => []);
        var plan = await registry.BuildPlanAsync(new WizardSelection { Workload = workload });
        plan.Selection.TargetFolder = @"C:\Installs\Fooocus";
        return plan;
    }

    private static InstallReportEntry Row(string operation) => new()
    {
        PlannedOperation = operation,
        Category = DiffusionNexus.Installer.SDK.Models.Installation.InstallReportCategory.Step,
        Outcome = DiffusionNexus.Installer.SDK.Models.Installation.InstallReportOutcome.Success
    };

    [Fact]
    public async Task Report_rows_recorded_during_the_run_are_visible_before_it_ends()
    {
        // The session subscribes through InstallationOptions.OnReportRow, which is what lets the
        // install screen's right-hand column fill row by row instead of appearing at the end.
        var seenMidRun = new List<string>();
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
            {
                options.OnReportRow!(Row("Setting up Git"));
                options.OnReportRow!(Row("Cloning main repository"));
                return Task.FromResult(InstallationResult.Success("done"));
            });

        var session = new InstallSession(orchestrator.Object);
        session.Changed += () => seenMidRun = [.. session.ReportRows.Select(r => r.PlannedOperation)];

        await session.StartAsync(await PlanAsync());

        session.ReportRows.Select(r => r.PlannedOperation)
            .Should().Equal(["Setting up Git", "Cloning main repository"]);
        seenMidRun.Should().NotBeEmpty("the rows are readable while the run is still going");
    }

    [Fact]
    public async Task The_finished_report_replaces_the_rows_streamed_during_the_run()
    {
        // The finished report is the authority: it carries the "not run" rows an aborted run adds
        // for steps it never reached, which by definition never streamed.
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
            {
                options.OnReportRow!(Row("Setting up Git"));
                return Task.FromResult(InstallationResult.Failure(
                    "failed", [Row("Setting up Git"), Row("Installing requirements")]));
            });

        var session = new InstallSession(orchestrator.Object);

        await session.StartAsync(await PlanAsync());

        session.ReportRows.Select(r => r.PlannedOperation)
            .Should().Equal(["Setting up Git", "Installing requirements"]);
    }

    [Fact]
    public async Task A_second_run_starts_from_an_empty_report()
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
            {
                options.OnReportRow!(Row("Setting up Git"));
                return Task.FromResult(InstallationResult.Success("done"));
            });

        var session = new InstallSession(orchestrator.Object);
        await session.StartAsync(await PlanAsync());
        await session.StartAsync(await PlanAsync());

        session.ReportRows.Should().ContainSingle("the second run's table must not open with the first run's rows");
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
        //
        // InstallFolderModule now pushes TargetFolder onto the selection eagerly (Task 3, so the
        // Content stage can scan the install folder before Confirm ever runs Contribute), so the
        // selection already carries the answer below -- the original argument-evaluation-order bug
        // this test guards against is now structurally impossible for TargetFolder. The assertion
        // that matters is still the one after StartAsync: the orchestrator gets the right folder.
        var settings = new Mock<IUserSettingsRepository>();
        settings.Setup(s => s.GetOrCreateForCurrentUserAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new UserSettings());
        var folderModule = new InstallFolderModule(settings.Object, new PreInstallationService());

        var workload = new InstallationConfiguration { Name = "Fooocus" };
        workload.Repository.Type = RepositoryType.Fooocus;

        var registry = new WizardModuleRegistry(() => [folderModule]);
        var plan = await registry.BuildPlanAsync(new WizardSelection { Workload = workload });

        // The module has an answer, but nothing has called ToOptions() yet.
        folderModule.TargetFolder = @"C:\Installs\Fooocus";
        plan.Selection.TargetFolder.Should().Be(@"C:\Installs\Fooocus", "the module pushes it eagerly");

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
                            IProgress<DownloadProgress>? _, Func<CancellationToken>? _,
                            CancellationToken token) =>
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

    // ---- The log file (issue #14) ---------------------------------------------------------------
    // What the 1.x wizard did: when a run ends, the whole log goes to a timestamped file in the
    // install folder, so a user can find and send it without the installer still being open.

    private static IInstallationOrchestrator LoggingOrchestrator(int lines, InstallationResult? result = null)
    {
        var orchestrator = new Mock<IInstallationOrchestrator>();
        orchestrator
            .Setup(o => o.InstallAsync(
                It.IsAny<InstallationConfiguration>(), It.IsAny<string>(), It.IsAny<InstallationOptions>(),
                It.IsAny<IProgress<InstallLogEntry>>(), It.IsAny<IProgress<InstallationProgress>>(),
                It.IsAny<IProgress<DownloadProgress>>(), It.IsAny<Func<CancellationToken>>(),
                It.IsAny<CancellationToken>()))
            .Returns((InstallationConfiguration _, string _, InstallationOptions _,
                      IProgress<InstallLogEntry> log, IProgress<InstallationProgress> _,
                      IProgress<DownloadProgress> _, Func<CancellationToken> _, CancellationToken _) =>
            {
                for (var i = 0; i < lines; i++)
                    log.Report(new InstallLogEntry { Timestamp = DateTime.UtcNow, Message = $"line {i}", Level = SdkLogLevel.Info });
                return Task.FromResult(result ?? InstallationResult.Success("done"));
            });
        return orchestrator.Object;
    }

    private static async Task<WizardPlan> PlanInAsync(string folder)
    {
        var plan = await PlanAsync();
        plan.Selection.TargetFolder = folder;
        return plan;
    }

    [Fact]
    public async Task A_finished_run_writes_the_whole_log_to_the_install_folder()
    {
        var folder = Directory.CreateTempSubdirectory("dn-log-").FullName;
        try
        {
            var session = new InstallSession(LoggingOrchestrator(3));
            await session.StartAsync(await PlanInAsync(folder));

            var file = Directory.GetFiles(folder, "installation-log-verbose-*.txt").Should().ContainSingle().Subject;
            var text = File.ReadAllText(file);
            text.Should().Contain("Installation Log");
            text.Should().Contain("Fooocus", "the header names the workload");
            text.Should().Contain(folder, "and the install folder");
            text.Should().Contain("line 0").And.Contain("line 2");
            text.Should().Contain("[Info]", "every line carries its level, as the live view shows it");
            text.Should().NotContain("earlier lines truncated");

            session.LogFilePath.Should().Be(file);
            session.LogLines.Should().Contain(l => l.Message.Contains("Log saved to") && l.Message.Contains(file),
                "the live log says where the file went, as the 1.x wizard did");
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    [Fact]
    public async Task A_failed_run_still_writes_its_log_with_the_outcome_in_the_header()
    {
        var folder = Directory.CreateTempSubdirectory("dn-log-").FullName;
        try
        {
            var session = new InstallSession(LoggingOrchestrator(1, InstallationResult.Failure("pip exploded")));
            await session.StartAsync(await PlanInAsync(folder));

            var text = File.ReadAllText(Directory.GetFiles(folder, "installation-log-verbose-*.txt").Single());
            text.Should().Contain("Failed").And.Contain("pip exploded");
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    [Fact]
    public async Task No_install_folder_means_no_file_and_a_run_that_still_ends_cleanly()
    {
        // An install that died before creating its folder has nowhere to put the file. Same as 1.x:
        // skip it, never fail the run over it.
        var missing = Path.Combine(Path.GetTempPath(), "dn-missing-" + Guid.NewGuid().ToString("N"));
        var session = new InstallSession(LoggingOrchestrator(1));

        await session.StartAsync(await PlanInAsync(missing));

        session.Phase.Should().Be(InstallPhase.Completed);
        session.LogFilePath.Should().BeNull();
        Directory.Exists(missing).Should().BeFalse("the session must not create install folders on its own");
    }

    [Fact]
    public async Task A_log_that_outgrew_the_buffer_says_so_in_the_file()
    {
        // The buffer keeps the newest 5000 lines. A file written from it after a long pip session
        // would otherwise look complete while missing its beginning.
        var folder = Directory.CreateTempSubdirectory("dn-log-").FullName;
        try
        {
            var session = new InstallSession(LoggingOrchestrator(InstallSession.MaxLogLines + 7));
            await session.StartAsync(await PlanInAsync(folder));

            var text = File.ReadAllText(Directory.GetFiles(folder, "installation-log-verbose-*.txt").Single());
            text.Should().Contain("7 earlier lines truncated");
            text.Should().NotContain($"] line 6{Environment.NewLine}").And.Contain($"] line 7{Environment.NewLine}");
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    [Fact]
    public async Task A_second_run_starts_with_no_log_file_path()
    {
        var folder = Directory.CreateTempSubdirectory("dn-log-").FullName;
        try
        {
            var session = new InstallSession(LoggingOrchestrator(1));
            await session.StartAsync(await PlanInAsync(folder));
            session.LogFilePath.Should().NotBeNull();

            var missing = Path.Combine(Path.GetTempPath(), "dn-missing-" + Guid.NewGuid().ToString("N"));
            await session.StartAsync(await PlanInAsync(missing));

            session.LogFilePath.Should().BeNull("the previous run's file is not this run's");
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    [Fact]
    public async Task The_log_snapshot_carries_the_lines_and_the_dropped_count_together()
    {
        // Review finding: read as two properties, the count could be taken after more lines were
        // dropped than the copied text is missing. One accessor, one lock.
        var missing = Path.Combine(Path.GetTempPath(), "dn-missing-" + Guid.NewGuid().ToString("N"));
        var session = new InstallSession(LoggingOrchestrator(InstallSession.MaxLogLines + 7));
        await session.StartAsync(await PlanInAsync(missing));

        var (lines, truncated) = session.SnapshotLog();

        lines.Should().HaveCount(InstallSession.MaxLogLines);
        lines[0].Message.Should().Be("line 7");
        truncated.Should().Be(7);
    }
}
