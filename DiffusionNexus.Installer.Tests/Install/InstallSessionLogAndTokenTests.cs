using DiffusionNexus.Installer.Core.Install;
using DiffusionNexus.Installer.Core.Wizard;
using DiffusionNexus.Installer.SDK.Models.Configuration;
using DiffusionNexus.Installer.SDK.Services;
using FluentAssertions;
using Moq;
using Xunit;
using SdkLogLevel = DiffusionNexus.Installer.SDK.Models.Enums.LogLevel;

namespace DiffusionNexus.Installer.Tests.Install;

/// <summary>
/// The two seams the live install view depends on: a bounded log read, and a token a prompt can be
/// cancelled through. Both exist because rendering and cancelling were reaching past the session.
/// </summary>
public class InstallSessionLogAndTokenTests
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

    private static Mock<IInstallationOrchestrator> Orchestrator(
        Func<IProgress<InstallLogEntry>, CancellationToken, Task<InstallationResult>> behaviour)
    {
        var orchestrator = new Mock<IInstallationOrchestrator>();
        orchestrator
            .Setup(o => o.InstallAsync(
                It.IsAny<InstallationConfiguration>(), It.IsAny<string>(), It.IsAny<InstallationOptions>(),
                It.IsAny<IProgress<InstallLogEntry>>(), It.IsAny<IProgress<InstallationProgress>>(),
                It.IsAny<IProgress<DownloadProgress>>(), It.IsAny<Func<CancellationToken>>(),
                It.IsAny<CancellationToken>()))
            .Returns((
                InstallationConfiguration _, string _, InstallationOptions _,
                IProgress<InstallLogEntry> log, IProgress<InstallationProgress> _,
                IProgress<DownloadProgress> _, Func<CancellationToken> _, CancellationToken ct)
                => behaviour(log, ct));
        return orchestrator;
    }

    private static InstallLogEntry Line(int i) =>
        new() { Timestamp = DateTime.UtcNow, Message = $"line {i}", Level = SdkLogLevel.Info };

    [Fact]
    public async Task Tail_returns_only_the_newest_lines()
    {
        var orchestrator = Orchestrator((log, _) =>
        {
            for (var i = 0; i < 50; i++) log.Report(Line(i));
            return Task.FromResult(InstallationResult.Success("done"));
        });

        var session = new InstallSession(orchestrator.Object);
        await session.StartAsync(await PlanAsync());

        var tail = session.Tail(10);

        tail.Should().HaveCount(10);
        tail[0].Message.Should().Be("line 40");
        tail[^1].Message.Should().Be("line 49", "the newest line must be last, as the view renders it");
    }

    [Fact]
    public async Task Tail_never_exceeds_what_the_buffer_holds()
    {
        var orchestrator = Orchestrator((log, _) =>
        {
            for (var i = 0; i < 3; i++) log.Report(Line(i));
            return Task.FromResult(InstallationResult.Success("done"));
        });

        var session = new InstallSession(orchestrator.Object);
        await session.StartAsync(await PlanAsync());

        session.Tail(1000).Should().HaveCount(3);
        session.Tail(0).Should().BeEmpty();
        session.Tail(-1).Should().BeEmpty();
    }

    [Fact]
    public async Task Cancel_completes_a_prompt_the_install_is_blocked_on()
    {
        // The reason RunToken exists. ShortcutManager awaits the conflict callback with no token of
        // its own, so without threading the session's token into the prompt a Cancel leaves the
        // pipeline thread parked forever on an answer that can never arrive -- the install looks
        // hung and the only exit is killing the process.
        var prompts = new Core.Host.ModalPromptService();
        var promptRaised = new TaskCompletionSource();

        var orchestrator = Orchestrator(async (_, ct) =>
        {
            promptRaised.SetResult();

            // Stands in for the shortcut-conflict callback: a prompt raised on the pipeline's
            // behalf, carrying the session's run token.
            var answer = await prompts.ConfirmAsync("Conflict", "Replace?", "Replace", "Keep", ct);
            return answer
                ? InstallationResult.Success("overwritten")
                : InstallationResult.Cancelled("cancelled");
        });

        var session = new InstallSession(orchestrator.Object);
        var run = session.StartAsync(await PlanAsync());

        await promptRaised.Task;
        prompts.IsOpen.Should().BeTrue();

        session.Cancel();

        // Completes rather than hanging: this is the whole assertion.
        await run.WaitAsync(TimeSpan.FromSeconds(5));

        prompts.IsOpen.Should().BeFalse("cancelling the run must close the dialog it was blocked on");
        session.Phase.Should().Be(InstallPhase.Cancelled);
    }

    [Fact]
    public async Task RunToken_is_cancelled_by_Cancel()
    {
        var observed = CancellationToken.None;
        var reached = new TaskCompletionSource();

        var orchestrator = Orchestrator(async (_, ct) =>
        {
            observed = ct;
            reached.SetResult();
            await Task.Delay(Timeout.Infinite, ct);
            return InstallationResult.Success("unreachable");
        });

        var session = new InstallSession(orchestrator.Object);
        var run = session.StartAsync(await PlanAsync());

        await reached.Task;
        session.RunToken.IsCancellationRequested.Should().BeFalse();

        session.Cancel();
        await run.WaitAsync(TimeSpan.FromSeconds(5));

        observed.IsCancellationRequested.Should().BeTrue();
        session.Phase.Should().Be(InstallPhase.Cancelled);
    }
}
