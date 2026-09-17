using Bunit;
using DiffusionNexus.Installer.Core.Host;
using DiffusionNexus.Installer.Core.Install;
using DiffusionNexus.Installer.Core.Wizard;
using DiffusionNexus.Installer.Electron.Components.Wizard;
using DiffusionNexus.Installer.SDK.Models.Configuration;
using DiffusionNexus.Installer.SDK.Services;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.JSInterop;
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
    private readonly Mock<IClipboard> _clipboard = new();

    public InstallStageTests()
    {
        Directory.CreateDirectory(_folder);

        _session.SetupGet(s => s.Phase).Returns(InstallPhase.Running);
        _session.Setup(s => s.Tail(It.IsAny<int>())).Returns([]);
        _session.Setup(s => s.SnapshotLog()).Returns(new InstallLogSnapshot([], 0));
        _session.SetupGet(s => s.ReportRows).Returns([]);
        _actions.SetupGet(a => a.CanCloseInstaller).Returns(true);
        _actions.Setup(a => a.CloseInstallerAsync()).Returns(Task.CompletedTask);
        _clipboard.SetupGet(c => c.IsAvailable).Returns(true);
        _clipboard.Setup(c => c.SetTextAsync(It.IsAny<string>())).Returns(Task.CompletedTask);

        Services.AddSingleton(_session.Object);
        Services.AddSingleton(_actions.Object);
        Services.AddSingleton(_clipboard.Object);

        // The log box hands itself to a script that keeps it scrolled to its newest line. bUnit
        // executes no JavaScript, so the module is planned rather than run -- without this every
        // render here fails on an unplanned interop call.
        JSInterop.SetupModule("./js/install-log.js")
            .SetupModule("follow", _ => true)
            .SetupVoid("dispose", _ => true)
            .SetVoidResult();
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
    public async Task The_log_is_handed_to_the_script_that_keeps_it_on_its_newest_line()
    {
        // A fixed-height scroll box keeps its position across Blazor's updates, so left alone the
        // left column shows the FIRST of the 300 lines it holds for the whole install.
        var run = await RunAsync();

        var stage = Render<InstallStage>(p => p.Add(x => x.Run, run));

        stage.WaitForAssertion(() => JSInterop.Invocations["follow"].Should().ContainSingle());
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
    public async Task The_progress_bar_gets_a_number_html_can_read_on_a_comma_decimal_machine()
    {
        // Blazor formats an attribute with the CURRENT culture, and nothing in the app overrides
        // the OS one: on a German machine 5/12 rendered as value="41,666...", which is not a
        // valid floating-point number, so <progress> fell back to indeterminate for every step
        // that does not divide evenly.
        var run = await RunAsync();
        _session.SetupGet(s => s.Progress).Returns(new InstallationProgress
        {
            CurrentStep = InstallationStep.InstallTorch,
            StepIndex = 5,
            TotalSteps = 12,
            Message = "Installing PyTorch..."
        });

        var before = System.Globalization.CultureInfo.CurrentCulture;
        System.Globalization.CultureInfo.CurrentCulture = System.Globalization.CultureInfo.GetCultureInfo("de-DE");
        try
        {
            var stage = Render<InstallStage>(p => p.Add(x => x.Run, run));

            var value = stage.Find("progress").GetAttribute("value");
            value.Should().NotContain(",");
            double.Parse(value!, System.Globalization.CultureInfo.InvariantCulture).Should().BeApproximately(41.67, 0.01);
        }
        finally
        {
            System.Globalization.CultureInfo.CurrentCulture = before;
        }
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

        var heading = stage.Find(".install-result h2");
        heading.TextContent.Trim().Should().Be("Installation failed");

        // The class, not just the words. app.css colours the outcome through it, and a heading that
        // renders the right text in the default colour looks like a successful install.
        heading.ClassList.Should().Contain("result-bad");
    }

    [Fact]
    public async Task A_successful_outcome_is_marked_as_one()
    {
        var run = await RunAsync();
        _session.SetupGet(s => s.Phase).Returns(InstallPhase.Completed);
        _session.SetupGet(s => s.Result).Returns(InstallationResult.Success("All done", _folder));

        var stage = Render<InstallStage>(p => p.Add(x => x.Run, run));

        var heading = stage.Find(".install-result h2");
        heading.TextContent.Trim().Should().Be("Installation complete");
        heading.ClassList.Should().Contain("result-ok");
    }

    [Fact]
    public async Task A_cancelled_run_is_not_dressed_up_as_a_success()
    {
        var run = await RunAsync();
        _session.SetupGet(s => s.Phase).Returns(InstallPhase.Cancelled);
        _session.SetupGet(s => s.Result).Returns(InstallationResult.Cancelled("Installation cancelled."));

        var stage = Render<InstallStage>(p => p.Add(x => x.Run, run));

        var heading = stage.Find(".install-result h2");
        heading.TextContent.Trim().Should().Be("Cancelled");
        heading.ClassList.Should().Contain("result-bad");
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

    // ---- Copy log (issue #14) --------------------------------------------------------------------

    private static InstallLogLine LogLine(string message) =>
        new(DateTimeOffset.UtcNow, message, DiffusionNexus.Installer.SDK.Models.Enums.LogLevel.Info);

    [Fact]
    public async Task The_whole_log_can_be_copied_while_the_install_is_still_running()
    {
        // The whole buffer, not the 300-line tail the screen shows: a user pasting a failure into
        // an issue needs what came before it.
        var run = await RunAsync();
        _session.Setup(s => s.SnapshotLog()).Returns(new InstallLogSnapshot([LogLine("first"), LogLine("last")], 0));
        var stage = Render<InstallStage>(p => p.Add(x => x.Run, run));

        stage.FindAll("button").Single(b => b.TextContent.Trim() == "Copy log").Click();

        _clipboard.Verify(c => c.SetTextAsync(It.Is<string>(t => t.Contains("first") && t.Contains("last") && t.Contains("[Info]"))), Times.Once);
        stage.WaitForAssertion(() => stage.Markup.Should().Contain("Copied"));
    }

    [Fact]
    public async Task A_copy_of_a_log_that_outgrew_the_buffer_says_so_at_the_top()
    {
        var run = await RunAsync();
        _session.Setup(s => s.SnapshotLog()).Returns(new InstallLogSnapshot([LogLine("kept")], 3));
        var stage = Render<InstallStage>(p => p.Add(x => x.Run, run));

        stage.FindAll("button").Single(b => b.TextContent.Trim() == "Copy log").Click();

        _clipboard.Verify(c => c.SetTextAsync(It.Is<string>(t =>
            t.IndexOf("3 earlier lines truncated", StringComparison.Ordinal) < t.IndexOf("kept", StringComparison.Ordinal))), Times.Once);
    }

    [Fact]
    public async Task The_copied_badge_goes_away_on_its_own()
    {
        // Review finding: left on, "Copied" would sit beside the heading for the rest of a
        // twenty-minute install, describing a clipboard that stopped matching the screen long ago.
        var run = await RunAsync();
        var stage = Render<InstallStage>(p => p
            .Add(x => x.Run, run)
            .Add(x => x.CopiedBadgeDuration, TimeSpan.FromMilliseconds(50)));

        stage.FindAll("button").Single(b => b.TextContent.Trim() == "Copy log").Click();

        stage.WaitForAssertion(() => stage.Markup.Should().Contain("Copied"));
        stage.WaitForAssertion(() => stage.Markup.Should().NotContain("Copied"), TimeSpan.FromSeconds(2));
    }

    [Fact]
    public async Task A_clipboard_that_refuses_says_so_instead_of_pretending()
    {
        var run = await RunAsync();
        _session.Setup(s => s.SnapshotLog()).Returns(new InstallLogSnapshot([LogLine("first")], 0));
        _clipboard.Setup(c => c.SetTextAsync(It.IsAny<string>())).ThrowsAsync(new InvalidOperationException("no clipboard"));
        var stage = Render<InstallStage>(p => p.Add(x => x.Run, run));

        stage.FindAll("button").Single(b => b.TextContent.Trim() == "Copy log").Click();

        stage.WaitForAssertion(() => stage.Find(".validation-error").TextContent.Should().Contain("Could not copy the log").And.Contain("no clipboard"));
    }

    [Fact]
    public async Task Without_a_clipboard_there_is_no_copy_button()
    {
        // Same rule as "Close installer": outside the Electron shell a button that silently does
        // nothing is worse than no button.
        var run = await RunAsync();
        _clipboard.SetupGet(c => c.IsAvailable).Returns(false);
        var stage = Render<InstallStage>(p => p.Add(x => x.Run, run));

        stage.FindAll("button").Should().NotContain(b => b.TextContent.Trim() == "Copy log");
    }

    [Fact]
    public async Task A_finished_install_says_where_its_log_file_went()
    {
        var run = await RunAsync();
        var file = Path.Combine(_folder, "installation-log-verbose-2026-09-16-14-30-02.txt");
        _session.SetupGet(s => s.Phase).Returns(InstallPhase.Completed);
        _session.SetupGet(s => s.Result).Returns(InstallationResult.Success("All done", _folder));
        _session.SetupGet(s => s.LogFilePath).Returns(file);

        var stage = Render<InstallStage>(p => p.Add(x => x.Run, run));

        stage.Find(".log-file").TextContent.Should().Contain("installation-log-verbose-2026-09-16-14-30-02.txt");
    }
}
