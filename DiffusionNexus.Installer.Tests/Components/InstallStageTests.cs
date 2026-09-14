using Bunit;
using DiffusionNexus.Installer.Core.Host;
using DiffusionNexus.Installer.Core.Install;
using DiffusionNexus.Installer.Core.Wizard;
using DiffusionNexus.Installer.Electron.Components.Wizard;
using DiffusionNexus.Installer.SDK.Models.Configuration;
using DiffusionNexus.Installer.SDK.Services;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using Xunit;
// Aliased, not imported: Models.Installation also carries an InstallationOptions that collides
// with the Services one this file uses.
using InstallReportCategory = DiffusionNexus.Installer.SDK.Models.Installation.InstallReportCategory;
using InstallReportEntry = DiffusionNexus.Installer.SDK.Models.Installation.InstallReportEntry;
using InstallReportOutcome = DiffusionNexus.Installer.SDK.Models.Installation.InstallReportOutcome;

namespace DiffusionNexus.Installer.Tests.Components;

/// <summary>
/// The install screen itself: the two columns, the report filling as the run goes, the step
/// counter, and the four buttons a finished install offers. Rendered directly rather than through
/// the page because every one of these is driven by Session.Changed, which is what this component
/// — and not the page — subscribes to.
/// </summary>
public class InstallStageTests : BunitContext, IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "dn-stage-" + Guid.NewGuid().ToString("N"));
    private readonly Mock<IInstallSession> _session = new();
    private readonly Mock<IPostInstallActions> _actions = new();

    public InstallStageTests()
    {
        Directory.CreateDirectory(_folder);

        _session.SetupGet(s => s.Phase).Returns(InstallPhase.Running);
        _session.Setup(s => s.Tail(It.IsAny<int>())).Returns([]);
        _session.SetupGet(s => s.ReportRows).Returns([]);
        _actions.SetupGet(a => a.CanCloseInstaller).Returns(true);
        _actions.Setup(a => a.CloseInstallerAsync()).Returns(Task.CompletedTask);

        Services.AddSingleton(_session.Object);
        Services.AddSingleton(_actions.Object);
    }

    void IDisposable.Dispose()
    {
        try { Directory.Delete(_folder, recursive: true); } catch { /* best-effort */ }
        base.Dispose();
        GC.SuppressFinalize(this);
    }

    private async Task<WizardRun> RunAsync()
    {
        var workload = new InstallationConfiguration { Name = "Fooocus" };
        workload.Repository.Type = RepositoryType.Fooocus;

        var plan = await new WizardModuleRegistry(() => [])
            .BuildPlanAsync(new WizardSelection { Workload = workload });
        plan.Selection.TargetFolder = _folder;

        // The session is already carrying this plan, so rendering rejoins the run instead of
        // starting it — the same path a circuit reconnect takes.
        _session.SetupGet(s => s.Plan).Returns(plan);

        return new WizardRun(plan, WizardStage.Install);
    }

    private static InstallReportEntry Row(string operation, InstallReportOutcome outcome = InstallReportOutcome.Success) =>
        new()
        {
            PlannedOperation = operation,
            Category = InstallReportCategory.Step,
            Outcome = outcome
        };

    /// <summary>Creates the launcher script "Start app &amp; close" looks for.</summary>
    private string CreateLauncher()
    {
        var launcher = Path.Combine(_folder, OperatingSystem.IsWindows() ? "run_nvidia.bat" : "run_nvidia.sh");
        File.WriteAllText(launcher, string.Empty);
        return launcher;
    }

    [Fact]
    public async Task The_run_and_its_result_sit_side_by_side()
    {
        var run = await RunAsync();

        var stage = Render<InstallStage>(p => p.Add(x => x.Run, run));

        var columns = stage.FindAll(".install-split > .panel");
        columns.Should().HaveCount(2, "the live install on the left, its result on the right");
        columns[0].TextContent.Should().Contain("Installing");
        columns[1].TextContent.Should().Contain("Result");
        stage.FindAll(".install-split .install-log").Should().HaveCount(1);
    }

    [Fact]
    public async Task The_report_fills_while_the_install_is_still_running()
    {
        // The whole point of streaming the rows. Before this the table was rendered from
        // Session.Result, which does not exist until the run is over.
        var run = await RunAsync();
        var stage = Render<InstallStage>(p => p.Add(x => x.Run, run));

        stage.FindAll(".report tbody tr").Should().BeEmpty();

        _session.SetupGet(s => s.ReportRows).Returns([Row("Setting up Git")]);
        _session.Raise(s => s.Changed += null);

        var cells = stage.FindAll(".install-result .report tbody tr td");
        cells[0].TextContent.Should().Be("Setting up Git");
        cells[1].TextContent.Should().Be("Success");
        stage.FindAll("button").Should().Contain(b => b.TextContent.Trim() == "Cancel installation",
            "the run is still going");
    }

    [Fact]
    public async Task A_twelve_step_install_ends_on_step_twelve_of_twelve()
    {
        // The pipeline's final report sets StepIndex to the step COUNT so the bar reaches 100%,
        // and a zero-based +1 turned that into "Step 13 of 12" at the end of every install.
        var run = await RunAsync();
        _session.SetupGet(s => s.Progress).Returns(new InstallationProgress
        {
            CurrentStep = InstallationStep.PostInstall,
            StepIndex = 12,
            TotalSteps = 12,
            Message = "Installation complete"
        });

        var stage = Render<InstallStage>(p => p.Add(x => x.Run, run));

        stage.Markup.Should().Contain("Step 12 of 12");
        stage.Markup.Should().NotContain("Step 13");
        stage.Markup.Should().Contain("Installation complete", "the final report's message is the truthful half");
    }

    [Fact]
    public async Task A_step_in_flight_is_numbered_from_one()
    {
        var run = await RunAsync();
        _session.SetupGet(s => s.Progress).Returns(new InstallationProgress
        {
            CurrentStep = InstallationStep.InstallTorch,
            StepIndex = 0,
            TotalSteps = 12,
            Message = "Installing PyTorch..."
        });

        var stage = Render<InstallStage>(p => p.Add(x => x.Run, run));

        stage.Markup.Should().Contain("Step 1 of 12");
        stage.Markup.Should().Contain("Installing PyTorch...", "the pipeline's own sentence reads better than the enum name");
        stage.Markup.Should().NotContain("InstallTorch");
    }

    [Fact]
    public async Task A_skipped_step_still_says_which_step_it_was()
    {
        // The pipeline sends a bare "Skipped" as the message, which on its own names nothing.
        var run = await RunAsync();
        _session.SetupGet(s => s.Progress).Returns(new InstallationProgress
        {
            CurrentStep = InstallationStep.InstallTriton,
            StepIndex = 5,
            TotalSteps = 12,
            Message = "Skipped"
        });

        var stage = Render<InstallStage>(p => p.Add(x => x.Run, run));

        stage.Markup.Should().Contain("InstallTriton");
        stage.Markup.Should().Contain("skipped");
    }

    [Fact]
    public async Task A_finished_install_offers_all_four_ways_on()
    {
        var run = await RunAsync();
        CreateLauncher();
        _session.SetupGet(s => s.Phase).Returns(InstallPhase.Completed);
        _session.SetupGet(s => s.Result).Returns(InstallationResult.Success("All done", _folder));

        var stage = Render<InstallStage>(p => p.Add(x => x.Run, run));

        stage.FindAll(".wizard-actions button").Select(b => b.TextContent.Trim())
            .Should().Equal(["Open folder", "Back to all software", "Close installer", "Start app & close"]);
    }

    [Fact]
    public async Task A_failed_install_does_not_offer_to_start_it()
    {
        // A half-built app is not worth launching, and the report beside the buttons is what the
        // user needs to read instead.
        var run = await RunAsync();
        CreateLauncher();
        _session.SetupGet(s => s.Phase).Returns(InstallPhase.Failed);
        _session.SetupGet(s => s.Result).Returns(InstallationResult.Failure("Installing requirements failed"));

        var stage = Render<InstallStage>(p => p.Add(x => x.Run, run));

        stage.FindAll(".wizard-actions button").Select(b => b.TextContent.Trim())
            .Should().Equal(["Open folder", "Back to all software", "Close installer"]);
        stage.Find(".install-result h2").TextContent.Trim().Should().Be("Installation failed");
    }

    [Fact]
    public async Task Nothing_to_launch_means_no_launch_button()
    {
        // Same finished-and-successful state, but no launcher script on disk.
        var run = await RunAsync();
        _session.SetupGet(s => s.Phase).Returns(InstallPhase.Completed);
        _session.SetupGet(s => s.Result).Returns(InstallationResult.Success("All done", _folder));

        var stage = Render<InstallStage>(p => p.Add(x => x.Run, run));

        stage.FindAll("button").Should().NotContain(b => b.TextContent.Trim() == "Start app & close");
    }

    [Fact]
    public async Task Outside_the_electron_shell_there_is_nothing_to_close()
    {
        var run = await RunAsync();
        _actions.SetupGet(a => a.CanCloseInstaller).Returns(false);
        _session.SetupGet(s => s.Phase).Returns(InstallPhase.Completed);
        _session.SetupGet(s => s.Result).Returns(InstallationResult.Success("All done", _folder));

        var stage = Render<InstallStage>(p => p.Add(x => x.Run, run));

        stage.FindAll("button").Should().NotContain(b => b.TextContent.Trim() == "Close installer");
    }

    [Fact]
    public async Task Start_app_and_close_starts_the_launcher_then_closes()
    {
        var run = await RunAsync();
        var launcher = CreateLauncher();
        _session.SetupGet(s => s.Phase).Returns(InstallPhase.Completed);
        _session.SetupGet(s => s.Result).Returns(InstallationResult.Success("All done", _folder));

        var stage = Render<InstallStage>(p => p.Add(x => x.Run, run));
        stage.FindAll("button").Single(b => b.TextContent.Trim() == "Start app & close").Click();

        _actions.Verify(a => a.LaunchApp(launcher), Times.Once);
        _actions.Verify(a => a.CloseInstallerAsync(), Times.Once);
    }

    [Fact]
    public async Task A_launcher_that_will_not_start_leaves_the_installer_open_and_says_so()
    {
        // Closing first would leave a user whose launcher failed with no installer, no app, and
        // no error message.
        var run = await RunAsync();
        CreateLauncher();
        _actions.Setup(a => a.LaunchApp(It.IsAny<string>())).Throws(new InvalidOperationException("blocked by policy"));
        _session.SetupGet(s => s.Phase).Returns(InstallPhase.Completed);
        _session.SetupGet(s => s.Result).Returns(InstallationResult.Success("All done", _folder));

        var stage = Render<InstallStage>(p => p.Add(x => x.Run, run));
        stage.FindAll("button").Single(b => b.TextContent.Trim() == "Start app & close").Click();

        _actions.Verify(a => a.CloseInstallerAsync(), Times.Never);
        stage.Find(".validation-error").TextContent.Should().Contain("blocked by policy");
    }

    [Fact]
    public async Task Open_folder_opens_the_folder_the_user_chose()
    {
        var run = await RunAsync();
        _session.SetupGet(s => s.Phase).Returns(InstallPhase.Completed);

        // The repository is a subdirectory the pipeline created; the button still opens the folder
        // the user picked, which is what they recognise.
        _session.SetupGet(s => s.Result)
            .Returns(InstallationResult.Success("All done", Path.Combine(_folder, "Fooocus")));

        var stage = Render<InstallStage>(p => p.Add(x => x.Run, run));
        stage.FindAll("button").Single(b => b.TextContent.Trim() == "Open folder").Click();

        _actions.Verify(a => a.OpenFolder(_folder), Times.Once);
    }
}
