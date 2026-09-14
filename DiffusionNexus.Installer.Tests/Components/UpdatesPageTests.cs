using Bunit;
using DiffusionNexus.Installer.Core.Install;
using DiffusionNexus.Installer.Core.Wizard;
using DiffusionNexus.Installer.Electron.Services;
using DiffusionNexus.Installer.SDK.Models.Configuration;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using Xunit;
using UpdatesPage = DiffusionNexus.Installer.Electron.Components.Pages.Home;

namespace DiffusionNexus.Installer.Tests.Components;

/// <summary>
/// The updates page is one click from every wizard stage now that the top bar rides along with
/// them, and its "Restart and install" button quits the app and swaps its own binary. Doing that
/// mid-install abandons a half-written install folder, and nothing else in the app guards it --
/// there is no NavigationLock and no window-close handler.
/// </summary>
public class UpdatesPageTests : BunitContext
{
    private Mock<IInstallSession> Register(InstallPhase phase, WizardPlan? plan = null)
    {
        var session = new Mock<IInstallSession>();
        session.SetupGet(s => s.Phase).Returns(phase);
        session.SetupGet(s => s.Plan).Returns(plan);

        Services.AddSingleton(session.Object);

        var log = new UpdaterLog();
        log.MarkUpdateReady();
        Services.AddSingleton(log);

        return session;
    }

    private static string Button => "Restart and install";

    [Fact]
    public void Offers_the_restart_when_nothing_is_installing()
    {
        Register(InstallPhase.Idle);

        var page = Render<UpdatesPage>();

        page.FindAll("button").Should().Contain(b => b.TextContent.Trim() == Button);
    }

    [Fact]
    public async Task Withholds_the_restart_while_an_install_is_running()
    {
        var workload = new InstallationConfiguration { Name = "Krea-2-Turbo" };
        var plan = await new WizardModuleRegistry(() => [])
            .BuildPlanAsync(new WizardSelection { Workload = workload });

        Register(InstallPhase.Running, plan);

        var page = Render<UpdatesPage>();

        page.FindAll("button").Should().NotContain(b => b.TextContent.Trim() == Button);

        // And says why, naming the install, rather than silently dropping the button the user
        // came here to press.
        page.Markup.Should().Contain("Krea-2-Turbo");
    }

    [Fact]
    public void Offers_it_again_once_the_install_finishes()
    {
        var session = Register(InstallPhase.Running);
        var page = Render<UpdatesPage>();
        page.FindAll("button").Should().NotContain(b => b.TextContent.Trim() == Button);

        session.SetupGet(s => s.Phase).Returns(InstallPhase.Completed);
        session.Raise(s => s.Changed += null);

        // The page subscribes to the session, not only to the updater log -- without that the
        // notice would still say "wait until it finishes" after it had.
        page.WaitForAssertion(() =>
            page.FindAll("button").Should().Contain(b => b.TextContent.Trim() == Button));
    }

    [Fact]
    public async Task Stops_listening_to_the_session_when_it_goes_away()
    {
        // The session is a singleton that outlives every component on the circuit, so a handler
        // left attached pins this page for the life of the app.
        var session = Register(InstallPhase.Idle);
        Render<UpdatesPage>();

        await DisposeComponentsAsync();

        session.VerifyRemove(s => s.Changed -= It.IsAny<Action>(), Times.Once);
    }
}
