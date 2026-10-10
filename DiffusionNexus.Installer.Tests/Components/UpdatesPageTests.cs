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
        Services.AddSingleton<ReturnTarget>();

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
            Channel = CatalogChannel.Preview,
            Workloads = new SectionState(3, "abc", new DateTimeOffset(2026, 9, 15, 19, 0, 0, TimeSpan.Zero)) { Channel = CatalogChannel.Stable },
            Workflows = new SectionState(3, "abc", new DateTimeOffset(2026, 9, 15, 19, 0, 0, TimeSpan.Zero)) { Channel = CatalogChannel.Stable },
        };

        // The section's recorded channel, not the stamp: one rule with the "still from" line and
        // the install report (PR #43 review).
        Render<UpdatesPage>().Find(".catalog-installed").TextContent.Should().Contain("v3 (Stable), applied 2026-09-15");
    }

    // A state written by SDK 2.0.0 records no channel per section; its stamp can name a channel
    // whose content never landed, so the row names none.
    [Fact]
    public void An_installed_catalog_without_a_recorded_channel_shows_only_its_version()
    {
        Register(InstallPhase.Idle);
        _catalog.Installed = new LocalCatalogState
        {
            Channel = CatalogChannel.Preview,
            Workloads = new SectionState(3, "abc", new DateTimeOffset(2026, 9, 15, 19, 0, 0, TimeSpan.Zero)),
        };

        Render<UpdatesPage>().Find(".catalog-installed").TextContent.Trim().Should().Be("v3, applied 2026-09-15");
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

        // The whole cell: no "Back to all software" after it, as a link, a button or text (#32).
        page.Find(".catalog-outcome").TextContent.Trim().Should().Be("Catalog updated to v4.");
        page.FindAll(".catalog-changes").Should().BeEmpty("what changed is now what is installed");
    }

    private static readonly Guid WizardId = Guid.Parse("6f9619ff-8b86-d011-b42d-00cf4fc964ff");
    private static string Wizard => $"install/{WizardId}";

    private string BackHref(IRenderedComponent<UpdatesPage> page) => page.Find("a.back-link").GetAttribute("href")!;

    // What the coordinator does when an apply lands: content moves the generation, whatever the phase.
    private void Lands(CatalogApplyResult result, CatalogUpdatePhase phase)
    {
        _catalog.LastApply = result;
        _catalog.ContentGeneration++;
        _catalog.Phase = phase;
        _catalog.RaiseChanged();
    }

    public static TheoryData<CatalogApplyResult, CatalogUpdatePhase> LandedApplies => new()
    {
        { new CatalogApplyResult(CatalogSections.All, CatalogSections.None, null), CatalogUpdatePhase.Applied },
        // Partial: the phase goes back to Checked, but the wizard is half-new all the same.
        { new CatalogApplyResult(CatalogSections.Workloads, CatalogSections.Workflows, "workflows/ locked"), CatalogUpdatePhase.Checked },
    };

    [Theory]
    [MemberData(nameof(LandedApplies))]
    public void An_apply_opened_from_a_wizard_sends_Back_home(CatalogApplyResult result, CatalogUpdatePhase phase)
    {
        // The wizard was built from the old catalog; returning to it would rebuild it from the new
        // one and drop every answer. Back is the only way home now that the link is gone (#32).
        Register(InstallPhase.Idle);
        Available();
        Services.GetRequiredService<ReturnTarget>().Remember(Wizard);
        _catalog.Phase = CatalogUpdatePhase.Applying;
        var page = Render<UpdatesPage>();
        BackHref(page).Should().Be("/" + Wizard);

        Lands(result, phase);

        page.WaitForAssertion(() => BackHref(page).Should().Be("/"));
    }

    [Fact]
    public async Task An_apply_keeps_Back_on_an_install_that_is_on_screen()
    {
        // A run's report or progress does not depend on the catalog: leaving it behind would lose it.
        var plan = await PlanAsync(WizardId);
        Register(InstallPhase.Completed, plan);
        Available();
        var target = Services.GetRequiredService<ReturnTarget>();
        target.Remember(Wizard);
        target.InstallOnScreen = plan;
        _catalog.Phase = CatalogUpdatePhase.Applying;
        var page = Render<UpdatesPage>();

        Lands(new CatalogApplyResult(CatalogSections.All, CatalogSections.None, null), CatalogUpdatePhase.Applied);

        page.WaitForAssertion(() => BackHref(page).Should().Be("/" + Wizard));
    }

    [Fact]
    public async Task A_report_left_behind_does_not_shield_the_screen_after_it()
    {
        // Leaving a report by the top bar does not clear InstallOnScreen; the screen the user went
        // on to was built from the old catalog like any other.
        var plan = await PlanAsync(WizardId);
        Register(InstallPhase.Completed, plan);
        Available();
        var target = Services.GetRequiredService<ReturnTarget>();
        target.InstallOnScreen = plan;
        target.Remember("software/ComfyUI");
        _catalog.Phase = CatalogUpdatePhase.Applying;
        var page = Render<UpdatesPage>();

        Lands(new CatalogApplyResult(CatalogSections.All, CatalogSections.None, null), CatalogUpdatePhase.Applied);

        page.WaitForAssertion(() => BackHref(page).Should().Be("/"));
    }

    private static Task<WizardPlan> PlanAsync(Guid workloadId) =>
        new WizardModuleRegistry(() => [])
            .BuildPlanAsync(new WizardSelection { Workload = new InstallationConfiguration { Id = workloadId, Name = "Krea-2-Turbo" } });

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
    public void Does_not_claim_nothing_changed_when_what_landed_is_unknown()
    {
        // The apply threw and the state could not be read back: a section may be in (PR #44 review).
        Register(InstallPhase.Idle);
        Available();
        _catalog.LastApply = new CatalogApplyResult(CatalogSections.None, CatalogSections.All, "unexpected");
        _catalog.LastApplyUncertain = true;

        var page = Render<UpdatesPage>();

        page.Find(".catalog-error").TextContent.Trim().Should().Be("The catalog update failed: unexpected. Part of it may already be installed.");
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
    public void Picking_a_channel_switches_it_and_checks_the_app_on_the_new_channel()
    {
        Register(InstallPhase.Idle, appUpdateReady: false);
        var page = Render<UpdatesPage>();

        page.Find(Radio(CatalogChannel.Preview)).Change("Preview");

        _catalog.ChannelSet.Should().Be(CatalogChannel.Preview);
        page.WaitForAssertion(() => page.Find(Radio(CatalogChannel.Preview)).HasAttribute("checked").Should().BeTrue());
        // The switch previewed and applied the catalog itself; a second catalog check would only
        // repeat it. The app follows the same channel and checks on its own.
        page.WaitForAssertion(() => _appUpdater.Calls.Should().Equal(["allowPrerelease=True", "check"]));
        _catalog.Checks.Should().Be(0);
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

    // ----- channel switch (#39): warn before, switch, keep, fall through -----

    private static string Normalized(AngleSharp.Dom.IElement element) =>
        System.Text.RegularExpressions.Regex.Replace(element.TextContent, @"\s+", " ").Trim();

    private static CatalogChannelSwitch StableSwitch(IReadOnlyList<WorkloadChange>? workloads = null, IReadOnlyList<WorkflowChange>? workflows = null)
    {
        var preview = CatalogChecks.Available(4, CatalogChannel.Stable,
            workloads ?? [CatalogChecks.WorkloadRemoved("Qwen-Image-2.1", "V1.0")],
            workflows ??
            [
                CatalogChecks.WorkflowUpdated("Upscale", "V2", "V1", "Krea-2-Turbo"),
                CatalogChecks.WorkflowUpdated("Inpaint", "V2", "V1", "Krea-2-Turbo"),
                CatalogChecks.WorkflowUpdated("Outpaint", "V2", "V1", "Krea-2-Turbo"),
            ]);
        return new CatalogChannelSwitch(preview, ChannelSwitchWarning.For(preview)!);
    }

    private void OnPreviewWithPendingSwitch(CatalogChannelSwitch? pending = null)
    {
        _catalog.Channel = CatalogChannel.Preview;
        _catalog.ChannelSource = CatalogChannelSource.Setting;
        _catalog.PendingSwitch = pending ?? StableSwitch();
    }

    [Fact]
    public void A_pending_switch_says_what_goes_away_before_anything_changes()
    {
        Register(InstallPhase.Idle);
        OnPreviewWithPendingSwitch();

        var page = Render<UpdatesPage>();

        var warning = page.Find(".channel-switch-warning");
        Normalized(warning.QuerySelector("p")!).Should().Be(
            "Stable is at v4. Switching removes Qwen-Image-2.1 and changes 3 workflows. Software you have already installed is not affected.");
        warning.QuerySelectorAll("strong").Select(s => s.TextContent).Should().Equal("Qwen-Image-2.1");
        warning.QuerySelectorAll("button").Select(b => b.TextContent.Trim()).Should().Equal("Switch to Stable", "Keep Preview");
        // The details under it are the same change list an update shows.
        warning.TextContent.Should().Contain("Krea-2-Turbo – Upscale").And.Contain("V2 → V1");
    }

    [Fact]
    public void While_a_switch_waits_the_radios_show_the_choice_and_cannot_be_changed()
    {
        Register(InstallPhase.Idle);
        OnPreviewWithPendingSwitch();

        var page = Render<UpdatesPage>();

        page.Find(Radio(CatalogChannel.Stable)).HasAttribute("checked").Should().BeTrue();
        page.Find(Radio(CatalogChannel.Stable)).HasAttribute("disabled").Should().BeTrue();
        page.Find(Radio(CatalogChannel.Preview)).HasAttribute("disabled").Should().BeTrue();
        page.FindAll("button").Single(b => b.TextContent.Trim() == "Check for updates").HasAttribute("disabled").Should().BeTrue();
    }

    [Fact]
    public void While_a_switch_waits_the_old_channels_update_is_not_offered()
    {
        Register(InstallPhase.Idle);
        Available();
        OnPreviewWithPendingSwitch();

        var page = Render<UpdatesPage>();

        page.FindAll("button").Should().NotContain(b => b.TextContent.Trim() == Apply);
        page.FindAll(".catalog-changes").Should().OnlyContain(c => c.Closest(".channel-switch-warning") != null);
    }

    [Theory]
    [InlineData(2, 0, "Stable is at v4. Switching removes A and B. Software you have already installed is not affected.")]
    [InlineData(3, 0, "Stable is at v4. Switching removes A, B and C. Software you have already installed is not affected.")]
    [InlineData(0, 1, "Stable is at v4. Switching changes 1 workflow. Software you have already installed is not affected.")]
    public void The_warning_reads_as_a_sentence(int removed, int changed, string expected)
    {
        Register(InstallPhase.Idle);
        OnPreviewWithPendingSwitch(StableSwitch(
            [.. new[] { "A", "B", "C" }.Take(removed).Select(n => CatalogChecks.WorkloadRemoved(n, "V1"))],
            [.. Enumerable.Range(0, changed).Select(i => CatalogChecks.WorkflowUpdated($"F{i}", "V2", "V1"))]));

        var page = Render<UpdatesPage>();

        Normalized(page.Find(".channel-switch-warning p")).Should().Be(expected);
    }

    [Fact]
    public void Switch_confirms_and_then_checks_the_app_on_the_new_channel()
    {
        Register(InstallPhase.Idle, appUpdateReady: false);
        OnPreviewWithPendingSwitch();
        var page = Render<UpdatesPage>();

        page.FindAll("button").Single(b => b.TextContent.Trim() == "Switch to Stable").Click();

        _catalog.Confirms.Should().Be(1);
        page.WaitForAssertion(() => _appUpdater.Calls.Should().Equal(["allowPrerelease=False", "check"]));
        page.FindAll(".channel-switch-warning").Should().BeEmpty();
        page.Find(Radio(CatalogChannel.Stable)).HasAttribute("checked").Should().BeTrue();
    }

    [Fact]
    public void Keep_drops_the_warning_saves_nothing_and_snaps_the_radio_back()
    {
        Register(InstallPhase.Idle);
        OnPreviewWithPendingSwitch();
        var page = Render<UpdatesPage>();

        page.FindAll("button").Single(b => b.TextContent.Trim() == "Keep Preview").Click();

        _catalog.Keeps.Should().Be(1);
        _catalog.Confirms.Should().Be(0);
        page.FindAll(".channel-switch-warning").Should().BeEmpty();
        page.Find(Radio(CatalogChannel.Preview)).HasAttribute("checked").Should().BeTrue();
        page.Find(Radio(CatalogChannel.Preview)).HasAttribute("disabled").Should().BeFalse();
        _appUpdater.Calls.Should().BeEmpty();
    }

    [Fact]
    public void A_switch_that_fails_to_save_says_so_and_keeps_the_channel()
    {
        Register(InstallPhase.Idle);
        OnPreviewWithPendingSwitch();
        _catalog.ChannelSaveFailure = new IOException("settings.json is locked");
        var page = Render<UpdatesPage>();

        page.FindAll("button").Single(b => b.TextContent.Trim() == "Switch to Stable").Click();

        page.Markup.Should().Contain("The channel could not be saved: settings.json is locked");
        page.Find(Radio(CatalogChannel.Preview)).HasAttribute("checked").Should().BeTrue();
        _appUpdater.Calls.Should().BeEmpty();
    }

    [Fact]
    public void Picking_a_channel_that_needs_an_answer_waits_for_it()
    {
        Register(InstallPhase.Idle);
        _catalog.Channel = CatalogChannel.Preview;
        _catalog.PendingOnSwitch = StableSwitch();
        var page = Render<UpdatesPage>();

        page.Find(Radio(CatalogChannel.Stable)).Change("Stable");

        page.WaitForAssertion(() => page.FindAll(".channel-switch-warning").Should().ContainSingle());
        page.FindAll(".validation-error").Should().BeEmpty("waiting for an answer is not a refusal");
        _appUpdater.Calls.Should().BeEmpty();
    }

    private void IncompleteSwitchToStable(CatalogUpdateCheck lastCheck)
    {
        _catalog.Channel = CatalogChannel.Stable;
        _catalog.ChannelSource = CatalogChannelSource.Setting;
        _catalog.SwitchIncompleteReason = "The installed catalog is still from Preview (v5).";
        _catalog.Installed = new LocalCatalogState
        {
            Channel = CatalogChannel.Preview,
            Workloads = new SectionState(5, "51e1684", DateTimeOffset.UtcNow) { Channel = CatalogChannel.Preview },
            Workflows = new SectionState(5, "51e1684", DateTimeOffset.UtcNow) { Channel = CatalogChannel.Preview },
        };
        _catalog.LastCheck = lastCheck;
        _catalog.Phase = CatalogUpdatePhase.Checked;
    }

    [Fact]
    public void An_incomplete_switch_says_what_is_still_installed_and_offers_a_retry()
    {
        Register(InstallPhase.Idle);
        IncompleteSwitchToStable(CatalogChecks.Outcome(CatalogUpdateOutcome.Failed, "No such host is known."));
        var page = Render<UpdatesPage>();

        var line = page.Find(".catalog-switch-incomplete");
        Normalized(line).Should().StartWith("You follow Stable. The installed catalog is still from Preview (v5).");
        line.QuerySelector("button")!.TextContent.Trim().Should().Be("Retry");

        line.QuerySelector("button")!.Click();

        _catalog.Checks.Should().Be(1);
    }

    [Fact]
    public void An_incomplete_switch_with_an_update_to_apply_offers_apply_as_the_retry()
    {
        Register(InstallPhase.Idle);
        IncompleteSwitchToStable(CatalogChecks.Available(4, CatalogChannel.Stable));
        var page = Render<UpdatesPage>();

        page.Find(".catalog-switch-incomplete").QuerySelector("button").Should().BeNull();
        page.FindAll("button").Should().Contain(b => b.TextContent.Trim() == Apply);
    }

    // The wording is InstalledCatalogDescription's (its tests cover the sections); the page puts
    // the channel followed in front of the coordinator's reason, nothing of its own.
    [Fact]
    public void The_incomplete_line_is_the_coordinators_reason_after_the_channel_followed()
    {
        Register(InstallPhase.Idle);
        IncompleteSwitchToStable(CatalogChecks.Available(4, CatalogChannel.Stable));
        _catalog.SwitchIncompleteReason = "The installed workflows (v5) are not from Stable yet.";

        Normalized(Render<UpdatesPage>().Find(".catalog-switch-incomplete")).Should()
            .StartWith("You follow Stable. The installed workflows (v5) are not from Stable yet.");
    }

    [Theory]
    [InlineData(CatalogUpdatePhase.Checking)]
    [InlineData(CatalogUpdatePhase.Applying)]
    public void The_incomplete_line_waits_while_the_coordinator_works(CatalogUpdatePhase phase)
    {
        Register(InstallPhase.Idle);
        IncompleteSwitchToStable(CatalogChecks.Available(4, CatalogChannel.Stable));
        _catalog.Phase = phase;

        Render<UpdatesPage>().FindAll(".catalog-switch-incomplete").Should().BeEmpty();
        _catalog.IncompleteReads.Should().Be(0, "not computed on every progress re-render either (PR #43 review)");
    }

    [Fact]
    public void A_completed_switch_shows_no_incomplete_line()
    {
        Register(InstallPhase.Idle);
        IncompleteSwitchToStable(CatalogChecks.Outcome(CatalogUpdateOutcome.UpToDate));
        _catalog.SwitchIncompleteReason = null;

        Render<UpdatesPage>().FindAll(".catalog-switch-incomplete").Should().BeEmpty();
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
        _catalog.Subscribers.Should().Be(2, "the page, and its Back link through ReturnTarget");

        await DisposeComponentsAsync();

        _catalog.Subscribers.Should().Be(0);
    }

    // ----- apply catalog updates automatically (#36) -----

    private const string AutoApply = "input[data-role='auto-apply']";

    [Fact]
    public void The_auto_apply_switch_sits_with_the_channel_and_is_off_by_default()
    {
        Register(InstallPhase.Idle);

        var page = Render<UpdatesPage>();

        var toggle = page.Find(".update-channel " + AutoApply);
        toggle.HasAttribute("checked").Should().BeFalse();
        toggle.HasAttribute("disabled").Should().BeFalse();
        page.Find(".auto-apply-switch").TextContent.Trim().Should().Be("Apply catalog updates automatically");
    }

    [Fact]
    public void Turning_the_switch_on_saves_it_and_says_what_it_does()
    {
        Register(InstallPhase.Idle);
        _catalog.Channel = CatalogChannel.Preview;
        var page = Render<UpdatesPage>();

        page.Find(AutoApply).Change(true);

        _catalog.AutoApplySet.Should().Equal(true);
        page.Find(AutoApply).HasAttribute("checked").Should().BeTrue();
        page.Find("#auto-apply-hint").TextContent.Should().Contain("finds a catalog update on Preview, it applies it without asking");
        page.FindAll(".auto-apply-error").Should().BeEmpty();
        _catalog.Applies.Should().Be(0, "turning it on applies nothing by itself");
    }

    [Fact]
    public void A_refused_change_says_so_and_shows_the_setting_still_in_effect()
    {
        Register(InstallPhase.Idle);
        _catalog.RefuseChannelChange = true;
        var page = Render<UpdatesPage>();

        page.Find(AutoApply).Change(true);

        page.Find(".auto-apply-error").TextContent.Should().Contain("The setting was not changed: a catalog check, update or channel switch is in progress");
        page.Find(AutoApply).HasAttribute("checked").Should().BeFalse();
    }

    [Fact]
    public void A_failed_save_shows_the_error_under_the_switch()
    {
        Register(InstallPhase.Idle);
        _catalog.AutoApplySaveFailure = new IOException("settings.json is locked");
        var page = Render<UpdatesPage>();

        page.Find(AutoApply).Change(true);

        page.Find(".auto-apply-error").TextContent.Should().Be("The setting could not be saved: settings.json is locked");
        page.Find(AutoApply).HasAttribute("checked").Should().BeFalse();
    }

    [Theory]
    [InlineData(CatalogUpdatePhase.Checking)]
    [InlineData(CatalogUpdatePhase.Applying)]
    public void Disables_the_switch_while_the_coordinator_would_refuse_it(CatalogUpdatePhase phase)
    {
        Register(InstallPhase.Idle);
        _catalog.Phase = phase;
        var page = Render<UpdatesPage>();

        page.Find(AutoApply).HasAttribute("disabled").Should().BeTrue();

        _catalog.Phase = CatalogUpdatePhase.Checked;
        _catalog.RaiseChanged();
        page.WaitForAssertion(() => page.Find(AutoApply).HasAttribute("disabled").Should().BeFalse());
    }

    [Fact]
    public void Disables_the_switch_while_a_channel_switch_waits_for_an_answer()
    {
        Register(InstallPhase.Idle);
        _catalog.PendingSwitch = StableSwitch();

        var page = Render<UpdatesPage>();

        page.Find(AutoApply).HasAttribute("disabled").Should().BeTrue();
    }

    [Fact]
    public void An_automatic_apply_shows_the_outcome_and_what_changed()
    {
        Register(InstallPhase.Idle);
        Available(CatalogChecks.WorkloadAdded("Flux-Krea", "V1.0"));
        _catalog.AutoApply = true;
        _catalog.Phase = CatalogUpdatePhase.Applied;
        _catalog.LastApply = new CatalogApplyResult(CatalogSections.All, CatalogSections.None, null);
        _catalog.LastApplyAutomatic = true;

        var page = Render<UpdatesPage>();

        page.Find(".catalog-outcome").TextContent.Trim().Should().Be("Catalog updated to v4 automatically.");
        page.FindAll(".catalog-change-name").Select(n => n.TextContent).Should().Equal("Flux-Krea");
        page.FindAll("button").Should().NotContain(b => b.TextContent.Trim() == Apply);
    }

    [Fact]
    public void A_running_automatic_apply_shows_progress_like_a_manual_one()
    {
        Register(InstallPhase.Idle);
        Available();
        _catalog.Phase = CatalogUpdatePhase.Applying;
        _catalog.LastApplyAutomatic = true;
        _catalog.Progress = new CatalogDownloadProgress(50, 100);

        var page = Render<UpdatesPage>();

        page.Find(".catalog-progress").TextContent.Trim().Should().Be("Downloading… 50%");
        page.FindAll(".catalog-changes").Should().ContainSingle("the list once, not again for the automatic apply");
    }

    [Fact]
    public void A_failed_automatic_apply_shows_the_error_and_offers_the_manual_apply()
    {
        Register(InstallPhase.Idle);
        Available();
        _catalog.AutoApply = true;
        _catalog.LastApply = new CatalogApplyResult(CatalogSections.None, CatalogSections.All, "sha256 mismatch");
        _catalog.LastApplyAutomatic = true;

        var page = Render<UpdatesPage>();

        page.Find(".catalog-error").TextContent.Should().Be("The catalog update failed: sha256 mismatch. Nothing was changed.");
        page.FindAll("button").Should().Contain(b => b.TextContent.Trim() == Apply);
    }
}
