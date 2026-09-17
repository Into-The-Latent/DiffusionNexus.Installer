using Bunit;
using DiffusionNexus.Installer.Core.Install;
using DiffusionNexus.Installer.Core.Updates;
using DiffusionNexus.Installer.Core.Wizard;
using DiffusionNexus.Installer.Electron.Services;
using DiffusionNexus.Installer.SDK.Catalog.Packaging;
using DiffusionNexus.Installer.SDK.Catalog.Updates;
using DiffusionNexus.Installer.SDK.Models.Configuration;
using DiffusionNexus.Installer.Tests.Support;
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
    private readonly StubCatalogUpdateCoordinator _catalog = new();
    private readonly FakeAppUpdaterShell _appUpdater = new();

    private UpdaterLog _log = new();

    private Mock<IInstallSession> Register(InstallPhase phase, WizardPlan? plan = null, bool appUpdateReady = true)
    {
        var session = new Mock<IInstallSession>();
        session.SetupGet(s => s.Phase).Returns(phase);
        session.SetupGet(s => s.Plan).Returns(plan);

        Services.AddSingleton(session.Object);

        _log = new UpdaterLog();
        if (appUpdateReady) _log.MarkUpdateReady("3.0.8");
        Services.AddSingleton(_log);

        Services.AddSingleton<ICatalogUpdateCoordinator>(_catalog);
        Services.AddSingleton(new AppUpdateChecker(_catalog, _appUpdater, _log, Mock.Of<IAppReleaseFeed>()));

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

    private static string Apply => "Apply catalog update";

    private void Available(params WorkloadChange[] workloads)
    {
        _catalog.LastCheck = CatalogChecks.Available(4, CatalogChannel.Preview, workloads.Length == 0 ? null : workloads);
        _catalog.Phase = CatalogUpdatePhase.Checked;
    }

    [Fact]
    public void Says_not_checked_yet_before_any_check()
    {
        Register(InstallPhase.Idle);

        var page = Render<UpdatesPage>();

        page.Find(".catalog-outcome").TextContent.Trim().Should().Be("Not checked yet.");
        page.Find(Radio(CatalogChannel.Stable)).HasAttribute("checked").Should().BeTrue();
        page.Find(".catalog-installed").TextContent.Should().Contain("unknown");
    }

    [Fact]
    public void Describes_what_is_installed()
    {
        Register(InstallPhase.Idle);
        _catalog.Installed = new LocalCatalogState
        {
            Channel = CatalogChannel.Stable,
            Workloads = new SectionState(3, "abc", new DateTimeOffset(2026, 9, 15, 19, 0, 0, TimeSpan.Zero)),
            Workflows = new SectionState(3, "abc", new DateTimeOffset(2026, 9, 15, 19, 0, 0, TimeSpan.Zero)),
        };

        Render<UpdatesPage>().Find(".catalog-installed").TextContent.Should().Contain("v3 (Stable), applied 2026-09-15");
    }

    [Fact]
    public void Lists_the_changes_and_offers_apply_when_an_update_is_available()
    {
        Register(InstallPhase.Idle);
        Available(CatalogChecks.WorkloadUpdated("Krea-2-Turbo", "V1.0", "V1.1"), CatalogChecks.WorkloadAdded("Ernie", "V1.0"));

        var page = Render<UpdatesPage>();

        page.Find(".catalog-outcome").TextContent.Should().Contain("Catalog v4 is available on Preview.");
        page.FindAll(".catalog-changes h3").Select(h => h.TextContent).Should().Equal("Added", "Updated");
        page.Markup.Should().Contain("Krea-2-Turbo").And.Contain("V1.0 → V1.1").And.Contain("Ernie");
        var button = page.FindAll("button").Single(b => b.TextContent.Trim() == Apply);
        button.HasAttribute("disabled").Should().BeFalse();

        button.Click();

        _catalog.Applies.Should().Be(1);
    }

    [Fact]
    public void Withholds_apply_while_an_install_runs_and_says_why()
    {
        Register(InstallPhase.Running);
        Available();
        _catalog.ApplyBlockedReason = "It can be applied once Krea-2-Turbo has finished.";

        var page = Render<UpdatesPage>();

        page.FindAll("button").Should().NotContain(b => b.TextContent.Trim() == Apply);
        page.Markup.Should().Contain("It can be applied once Krea-2-Turbo has finished.");
    }

    [Theory]
    [InlineData(50L, 100L, "Downloading… 50%")]
    [InlineData(3_250_000L, null, "Downloading… 3.1 MB")]
    public void Shows_progress_while_applying(long received, long? total, string expected)
    {
        Register(InstallPhase.Idle);
        Available();
        _catalog.Phase = CatalogUpdatePhase.Applying;
        _catalog.Progress = new CatalogDownloadProgress(received, total);

        var page = Render<UpdatesPage>();

        page.Find(".catalog-progress").TextContent.Trim().Should().Be(expected);
        page.FindAll("button").Should().NotContain(b => b.TextContent.Trim() == Apply);
    }

    [Fact]
    public void Reports_the_new_version_after_a_successful_apply()
    {
        Register(InstallPhase.Idle);
        Available();
        _catalog.Phase = CatalogUpdatePhase.Applied;
        _catalog.LastApply = new CatalogApplyResult(CatalogSections.All, CatalogSections.None, null);

        var page = Render<UpdatesPage>();

        page.Find(".catalog-outcome").TextContent.Should().Contain("Catalog updated to v4.");
        page.Find(".catalog-outcome a[href='/']").TextContent.Should().Be("Back to all software");
        page.FindAll(".catalog-changes").Should().BeEmpty("what changed is now what is installed");
    }

    [Fact]
    public void Reports_a_failed_apply_and_offers_a_retry()
    {
        Register(InstallPhase.Idle);
        Available();
        _catalog.LastApply = new CatalogApplyResult(CatalogSections.None, CatalogSections.All, "sha256 mismatch");

        var page = Render<UpdatesPage>();

        page.Find(".catalog-error").TextContent.Should().Contain("The catalog update failed: sha256 mismatch. Nothing was changed.");
        page.FindAll("button").Should().Contain(b => b.TextContent.Trim() == Apply);
    }

    [Fact]
    public void Names_the_section_that_did_land_on_a_partial_apply()
    {
        Register(InstallPhase.Idle);
        Available();
        _catalog.LastApply = new CatalogApplyResult(CatalogSections.Workloads, CatalogSections.Workflows, "workflows/ locked");

        Render<UpdatesPage>().Find(".catalog-error").TextContent.Should().Contain("Workloads were updated; workflows failed: workflows/ locked");
    }

    [Theory]
    [InlineData(CatalogUpdateOutcome.UpToDate, null, "The catalog is up to date.")]
    [InlineData(CatalogUpdateOutcome.RequiresNewerSoftware, null, "This catalog update needs a newer version of the installer. Install the app update above first.")]
    [InlineData(CatalogUpdateOutcome.Failed, "HTTP 503", "The catalog check failed: HTTP 503")]
    public void Each_outcome_has_its_own_line(CatalogUpdateOutcome outcome, string? error, string expected)
    {
        Register(InstallPhase.Idle);
        _catalog.LastCheck = CatalogChecks.Outcome(outcome, error);
        _catalog.Phase = CatalogUpdatePhase.Checked;

        Render<UpdatesPage>().Find(".catalog-outcome").TextContent.Trim().Should().Be(expected);
    }

    [Fact]
    public void An_active_override_names_its_path()
    {
        Register(InstallPhase.Idle);
        _catalog.LastCheck = CatalogChecks.Outcome(CatalogUpdateOutcome.OverrideActive);
        _catalog.Phase = CatalogUpdatePhase.Checked;
        _catalog.OverridePath = @"E:\Repos\DiffusionNexus.Catalog";

        Render<UpdatesPage>().Find(".catalog-outcome").TextContent.Should()
            .Contain(@"a local catalog override is active at E:\Repos\DiffusionNexus.Catalog");
    }

    [Fact]
    public void Says_checking_while_a_check_runs()
    {
        Register(InstallPhase.Idle);
        _catalog.Phase = CatalogUpdatePhase.Checking;

        Render<UpdatesPage>().Find(".catalog-outcome").TextContent.Trim().Should().Be("Checking the catalog…");
    }

    [Fact]
    public void A_re_check_hides_the_previous_change_list_and_apply_button()
    {
        // The previous check found an update, so UpdateAvailable and LastCheck are both still
        // set while the re-check runs -- only Phase has moved to Checking. Without also gating on
        // Phase, the stale change list and Apply button would render right under "Checking the
        // catalog…", offering to apply content the running check might be about to replace.
        Register(InstallPhase.Idle);
        Available(CatalogChecks.WorkloadUpdated("Krea-2-Turbo", "V1.0", "V1.1"));
        _catalog.Phase = CatalogUpdatePhase.Checking;

        var page = Render<UpdatesPage>();

        page.Find(".catalog-outcome").TextContent.Trim().Should().Be("Checking the catalog…");
        page.FindAll(".catalog-changes").Should().BeEmpty();
        page.FindAll("button").Should().NotContain(b => b.TextContent.Trim() == Apply);
    }

    // ----- update channel: one picker for the app and the catalog -----

    private static string Radio(CatalogChannel channel) => $"input[name='update-channel'][value='{channel}']";

    [Fact]
    public void Shows_the_channel_the_coordinator_follows()
    {
        Register(InstallPhase.Idle);
        _catalog.Channel = CatalogChannel.Preview;

        var page = Render<UpdatesPage>();

        page.Find(Radio(CatalogChannel.Preview)).HasAttribute("checked").Should().BeTrue();
        page.Find(Radio(CatalogChannel.Stable)).HasAttribute("checked").Should().BeFalse();
    }

    // Shown once, for both. Two "Following: Stable" lines in two sections read as two settings.
    [Fact]
    public void The_channel_is_offered_once_in_our_words()
    {
        Register(InstallPhase.Idle);

        var page = Render<UpdatesPage>();

        page.FindAll("input[name='update-channel']").Should().HaveCount(2);
        page.Markup.Should().NotContain("Following:");
        var section = page.Find(".update-channel").TextContent;
        section.Should().Contain("Stable").And.Contain("Preview");
        // electron-updater's vocabulary never reaches the user.
        section.Should().NotContainAny("latest", "beta", "prerelease");
    }

    // The updater never downgrades, so a user leaving Preview needs to know the newer build stays.
    [Fact]
    public void The_hint_says_what_preview_is_and_that_leaving_it_does_not_downgrade()
    {
        Register(InstallPhase.Idle);

        var hint = Render<UpdatesPage>().Find(".update-channel .panel-hint").TextContent;

        hint.Should().Contain("before everyone else").And.Contain("until Stable catches up");
    }

    [Fact]
    public void The_radios_follow_a_switch_made_elsewhere()
    {
        Register(InstallPhase.Idle);
        var page = Render<UpdatesPage>();

        _catalog.Channel = CatalogChannel.Preview;
        _catalog.RaiseChanged();

        page.WaitForAssertion(() => page.Find(Radio(CatalogChannel.Preview)).HasAttribute("checked").Should().BeTrue());
    }

    [Fact]
    public void Picking_a_channel_saves_it_and_checks_both_on_the_new_channel()
    {
        Register(InstallPhase.Idle, appUpdateReady: false);
        var page = Render<UpdatesPage>();

        page.Find(Radio(CatalogChannel.Preview)).Change("Preview");

        _catalog.ChannelSet.Should().Be(CatalogChannel.Preview);
        page.WaitForAssertion(() => page.Find(Radio(CatalogChannel.Preview)).HasAttribute("checked").Should().BeTrue());
        // A switch forgets the last check, so without a fresh one the page would say nothing.
        page.WaitForAssertion(() => _catalog.Checks.Should().Be(1));
        _appUpdater.Calls.Should().Equal(["allowPrerelease=True", "check"]);
        page.FindAll(".validation-error").Should().BeEmpty();
    }

    [Fact]
    public void Disables_the_radios_when_the_environment_pins_the_channel()
    {
        Register(InstallPhase.Idle);
        _catalog.Channel = CatalogChannel.Preview;
        _catalog.ChannelSource = CatalogChannelSource.Environment;

        var page = Render<UpdatesPage>();

        page.Find(Radio(CatalogChannel.Preview)).HasAttribute("disabled").Should().BeTrue();
        page.Find(Radio(CatalogChannel.Stable)).HasAttribute("disabled").Should().BeTrue();
        page.Markup.Should().Contain("Set by DIFFUSIONNEXUS_CATALOG_CHANNEL for this run");
    }

    [Fact]
    public void A_refused_switch_says_so_checks_nothing_and_still_renders_the_channel_in_effect()
    {
        // The coordinator refuses silently (no Changed). What this cannot show: bUnit rebuilds its
        // DOM on every render, so the browser keeping the clicked dot (Blazor patches nothing when
        // "checked" renders the same values) is invisible here -- the @key on the radios handles
        // that, and docs/manual-smoke.md section 7 is where it is proven.
        Register(InstallPhase.Idle);
        _catalog.RefuseChannelChange = true;
        var page = Render<UpdatesPage>();

        page.Find(Radio(CatalogChannel.Preview)).Change("Preview");

        page.Markup.Should().Contain("The channel was not switched");
        page.Find(Radio(CatalogChannel.Stable)).HasAttribute("checked").Should().BeTrue();
        page.Find(Radio(CatalogChannel.Preview)).HasAttribute("checked").Should().BeFalse();
        _catalog.Checks.Should().Be(0);
        _appUpdater.Calls.Should().BeEmpty();
    }

    [Fact]
    public void A_failed_save_shows_the_error_and_the_channel_still_in_effect()
    {
        Register(InstallPhase.Idle);
        _catalog.ChannelSaveFailure = new IOException("settings.json is locked");
        var page = Render<UpdatesPage>();

        page.Find(Radio(CatalogChannel.Preview)).Change("Preview");

        page.Markup.Should().Contain("The channel could not be saved: settings.json is locked");
        page.Find(Radio(CatalogChannel.Stable)).HasAttribute("checked").Should().BeTrue();
        _catalog.Checks.Should().Be(0);
    }

    [Theory]
    [InlineData(CatalogUpdatePhase.Checking)]
    [InlineData(CatalogUpdatePhase.Applying)]
    public void Disables_the_radios_while_the_coordinator_would_refuse_a_switch(CatalogUpdatePhase phase)
    {
        Register(InstallPhase.Idle);
        _catalog.Phase = phase;
        var page = Render<UpdatesPage>();

        page.Find(Radio(CatalogChannel.Preview)).HasAttribute("disabled").Should().BeTrue();

        _catalog.Phase = CatalogUpdatePhase.Checked;
        _catalog.RaiseChanged();

        page.WaitForAssertion(() => page.Find(Radio(CatalogChannel.Preview)).HasAttribute("disabled").Should().BeFalse());
    }

    // PR #23 review: the catalog check finishes fast, the app check (feed read, pin, electron)
    // does not. Radios that come back in between invite a second switch under a running check.
    [Fact]
    public void The_radios_stay_disabled_until_the_app_check_is_done_too()
    {
        Register(InstallPhase.Idle, appUpdateReady: false);
        _appUpdater.CheckGate = new TaskCompletionSource();
        var page = Render<UpdatesPage>();

        page.Find(Radio(CatalogChannel.Preview)).Change("Preview");

        // The stub coordinator never leaves Idle, so only the app check can be holding these.
        page.WaitForAssertion(() => page.Find(Radio(CatalogChannel.Stable)).HasAttribute("disabled").Should().BeTrue());

        _appUpdater.CheckGate.SetResult();

        page.WaitForAssertion(() => page.Find(Radio(CatalogChannel.Stable)).HasAttribute("disabled").Should().BeFalse());
    }

    // ----- the app row, next to the catalog row -----

    [Fact]
    public void The_app_row_shows_the_version_the_rest_of_the_app_shows()
    {
        Register(InstallPhase.Idle);

        Render<UpdatesPage>().Find(".app-version").TextContent.Trim().Should().Be($"v{AppVersion.Display}");
    }

    [Theory]
    [InlineData("not-checked", "Not checked yet.")]
    [InlineData("unavailable", "App updates are checked only inside the Electron shell.")]
    [InlineData("not-installed", "This build is not installed, so there is no app update to check for.")]
    [InlineData("checking", "Checking the app…")]
    [InlineData("up-to-date", "The app is up to date.")]
    [InlineData("found", "App v3.0.8 is available. Downloading…")]
    [InlineData("downloading", "App v3.0.8 is available. Downloading… 42%")]
    [InlineData("ready", "App v3.0.8 is ready to install.")]
    [InlineData("failed", "The app check failed: offline")]
    public void Each_app_state_has_its_own_line(string state, string expected)
    {
        Register(InstallPhase.Idle, appUpdateReady: false);
        switch (state)
        {
            case "unavailable": _log.MarkUnavailable(); break;
            case "not-installed": _log.MarkNotInstalledBuild(); break;
            case "checking": _log.MarkChecking(); break;
            case "up-to-date": _log.MarkUpToDate(); break;
            case "found": _log.MarkAvailable("3.0.8"); break;
            case "downloading": _log.MarkAvailable("3.0.8"); _log.MarkProgress(42); break;
            case "ready": _log.MarkUpdateReady("3.0.8"); break;
            case "failed": _log.MarkFailed("offline"); break;
        }

        Render<UpdatesPage>().Find(".app-status").TextContent.Trim().Should().Be(expected);
    }

    [Fact]
    public void The_app_row_follows_the_updater()
    {
        Register(InstallPhase.Idle, appUpdateReady: false);
        var page = Render<UpdatesPage>();

        _log.MarkAvailable("3.0.8");

        page.WaitForAssertion(() => page.Find(".app-status").TextContent.Should().Contain("v3.0.8 is available"));
    }

    [Fact]
    public void The_check_button_checks_the_app_on_the_channel_the_catalog_follows()
    {
        Register(InstallPhase.Idle);
        _catalog.Channel = CatalogChannel.Preview;
        var page = Render<UpdatesPage>();

        page.FindAll("button").Single(b => b.TextContent.Trim() == "Check for updates").Click();

        _appUpdater.Calls.Should().Equal(["allowPrerelease=True", "check"]);
        _catalog.Checks.Should().Be(1);
    }

    [Fact]
    public void The_check_button_runs_the_catalog_check_even_outside_electron()
    {
        // ElectronHost.IsActive is false under test, which used to disable the button outright.
        // The catalog check has no Electron dependency, so the button now always works for it.
        Register(InstallPhase.Idle);
        var page = Render<UpdatesPage>();
        var button = page.FindAll("button").Single(b => b.TextContent.Trim() == "Check for updates");
        button.HasAttribute("disabled").Should().BeFalse();

        button.Click();

        _catalog.Checks.Should().Be(1);
    }

    [Fact]
    public void Re_renders_when_the_coordinator_changes()
    {
        Register(InstallPhase.Idle);
        var page = Render<UpdatesPage>();
        page.Find(".catalog-outcome").TextContent.Trim().Should().Be("Not checked yet.");

        Available();
        _catalog.RaiseChanged();

        page.WaitForAssertion(() => page.Find(".catalog-outcome").TextContent.Should().Contain("Catalog v4 is available"));
    }

    [Fact]
    public async Task Stops_listening_to_the_coordinator_when_disposed()
    {
        Register(InstallPhase.Idle);
        Render<UpdatesPage>();
        _catalog.Subscribers.Should().Be(1);

        await DisposeComponentsAsync();

        _catalog.Subscribers.Should().Be(0);
    }
}
