using DiffusionNexus.Installer.Core.Install;
using DiffusionNexus.Installer.Core.Updates;
using DiffusionNexus.Installer.Core.Wizard;
using DiffusionNexus.Installer.SDK.Catalog.Packaging;
using DiffusionNexus.Installer.SDK.Catalog.Updates;
using DiffusionNexus.Installer.SDK.Models.Configuration;
using DiffusionNexus.Installer.SDK.Models.Installation;
using DiffusionNexus.Installer.SDK.Services.Settings;
using DiffusionNexus.Installer.Tests.Support;
using FluentAssertions;
using Moq;
using Xunit;

namespace DiffusionNexus.Installer.Tests.Updates;

public sealed class CatalogUpdateCoordinatorTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"dn-coordinator-{Guid.NewGuid():N}");
    private readonly FakeCatalogUpdateService _service = new();
    private readonly CatalogOptions _options;
    private readonly Mock<IUserSettingsRepository> _settings = new();
    private readonly Mock<IInstallSession> _session = new();
    private readonly UserSettings _saved = new() { UserName = "tester" };
    private string? _environment;

    public CatalogUpdateCoordinatorTests()
    {
        Directory.CreateDirectory(_dir);
        _options = new CatalogOptions { InstalledCatalogPath = Path.Combine(_dir, "catalog") };
        _settings.Setup(s => s.GetOrCreateForCurrentUserAsync(It.IsAny<CancellationToken>())).ReturnsAsync(_saved);
        _settings.Setup(s => s.SaveAsync(It.IsAny<UserSettings>(), It.IsAny<CancellationToken>()))
                 .ReturnsAsync((UserSettings s, CancellationToken _) => s);
        _session.SetupGet(s => s.Phase).Returns(InstallPhase.Idle);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    private CatalogUpdateCoordinator Create() =>
        new(_service, _options, _settings.Object, _session.Object, () => _environment);

    private void WriteInstalledState(int version) =>
        new LocalCatalogState { Workloads = new SectionState(version, "abc", DateTimeOffset.UtcNow) }
            .Save(_options.InstalledCatalogPath);

    // ----- channel -----

    [Fact]
    public async Task The_first_check_resolves_the_saved_channel_into_the_sdk_options()
    {
        _saved.CatalogChannel = "Preview";
        using var coordinator = Create();

        await coordinator.CheckAsync();

        coordinator.Channel.Should().Be(CatalogChannel.Preview);
        coordinator.ChannelSource.Should().Be(CatalogChannelSource.Setting);
        _options.Channel.Should().Be(CatalogChannel.Preview, "CheckAsync in the SDK reads this and nothing else");
    }

    [Fact]
    public async Task The_environment_variable_wins_over_the_saved_channel()
    {
        _saved.CatalogChannel = "Preview";
        _environment = "stable";
        using var coordinator = Create();

        await coordinator.CheckAsync();

        (coordinator.Channel, coordinator.ChannelSource).Should().Be((CatalogChannel.Stable, CatalogChannelSource.Environment));
    }

    [Fact]
    public async Task An_unreadable_settings_file_falls_back_to_stable_and_still_checks()
    {
        _settings.Setup(s => s.GetOrCreateForCurrentUserAsync(It.IsAny<CancellationToken>())).ThrowsAsync(new IOException("locked"));
        using var coordinator = Create();

        await coordinator.CheckAsync();

        coordinator.ChannelSource.Should().Be(CatalogChannelSource.Default);
        _service.CheckCalls.Should().Be(1);
    }

    // The app updater follows the same channel and runs its startup check on its own, so it has
    // to be able to ask for the channel without a catalog check having happened first.
    [Fact]
    public async Task The_channel_can_be_resolved_without_running_a_check()
    {
        _saved.CatalogChannel = "Preview";
        using var coordinator = Create();

        var channel = await coordinator.ResolveChannelAsync();

        channel.Should().Be(CatalogChannel.Preview);
        coordinator.ChannelSource.Should().Be(CatalogChannelSource.Setting);
        _options.Channel.Should().Be(CatalogChannel.Preview);
        _service.CheckCalls.Should().Be(0);
        coordinator.Phase.Should().Be(CatalogUpdatePhase.Idle);
    }

    [Fact]
    public async Task Resolving_the_channel_never_throws_and_falls_back_to_stable()
    {
        _settings.Setup(s => s.GetOrCreateForCurrentUserAsync(It.IsAny<CancellationToken>())).ThrowsAsync(new InvalidOperationException("boom"));
        using var coordinator = Create();

        var channel = await coordinator.ResolveChannelAsync();

        channel.Should().Be(CatalogChannel.Stable);
    }

    [Fact]
    public async Task Resolving_the_channel_reads_the_settings_once()
    {
        using var coordinator = Create();

        await coordinator.ResolveChannelAsync();
        await coordinator.ResolveChannelAsync();
        await coordinator.CheckAsync();

        _settings.Verify(s => s.GetOrCreateForCurrentUserAsync(It.IsAny<CancellationToken>()), Times.Once);
    }

    // A resolve that read the OLD saved value before a switch saved the new one must not land its
    // answer after the switch: the app updater would then follow the channel the user just left.
    [Fact]
    public async Task A_resolve_that_straddles_a_channel_switch_does_not_undo_it()
    {
        var read = new TaskCompletionSource<UserSettings>();
        _settings.SetupSequence(s => s.GetOrCreateForCurrentUserAsync(It.IsAny<CancellationToken>()))
                 .Returns(read.Task)
                 .ReturnsAsync(_saved)
                 .ReturnsAsync(_saved);
        using var coordinator = Create();

        var resolving = coordinator.ResolveChannelAsync();
        await coordinator.SwitchChannelAsync(CatalogChannel.Preview);
        read.SetResult(new UserSettings { UserName = "tester", CatalogChannel = "Stable" });

        (await resolving).Should().Be(CatalogChannel.Preview);
        coordinator.Channel.Should().Be(CatalogChannel.Preview);
        _options.Channel.Should().Be(CatalogChannel.Preview);
    }

    [Fact]
    public void The_override_path_is_the_sdk_options_override_path()
    {
        _options.LocalOverridePath = @"E:\Repos\DiffusionNexus.Catalog";
        using var coordinator = Create();
        coordinator.OverridePath.Should().Be(@"E:\Repos\DiffusionNexus.Catalog");
    }

    // ----- check -----

    [Fact]
    public async Task A_check_stores_the_outcome_reloads_the_installed_state_and_ends_checked()
    {
        WriteInstalledState(3);
        _service.NextCheck = () => CatalogChecks.Available(4);
        using var coordinator = Create();
        var raised = 0;
        coordinator.Changed += () => raised++;

        await coordinator.CheckAsync();

        coordinator.Phase.Should().Be(CatalogUpdatePhase.Checked);
        coordinator.LastCheck!.Outcome.Should().Be(CatalogUpdateOutcome.UpdatesAvailable);
        coordinator.Installed!.HighestCatalogVersion.Should().Be(3);
        coordinator.UpdateAvailable.Should().BeTrue();
        coordinator.CanApply.Should().BeTrue();
        raised.Should().BeGreaterThanOrEqualTo(2, "once entering Checking, once leaving it");
    }

    [Fact]
    public async Task An_up_to_date_check_offers_nothing()
    {
        using var coordinator = Create();
        await coordinator.CheckAsync();

        coordinator.UpdateAvailable.Should().BeFalse();
        coordinator.CanApply.Should().BeFalse();
        coordinator.ApplyBlockedReason.Should().BeNull();
    }

    [Fact]
    public async Task A_second_check_while_one_is_running_joins_it_instead_of_calling_the_sdk_again()
    {
        _service.HoldCheck = new TaskCompletionSource();
        using var coordinator = Create();

        var first = coordinator.CheckAsync();
        var second = coordinator.CheckAsync();
        coordinator.Phase.Should().Be(CatalogUpdatePhase.Checking);

        _service.HoldCheck.SetResult();
        await Task.WhenAll(first, second);

        _service.CheckCalls.Should().Be(1);
        coordinator.Phase.Should().Be(CatalogUpdatePhase.Checked);
    }

    // ----- apply refusals (the download itself is Task 5) -----

    [Fact]
    public async Task Apply_is_refused_before_a_check_found_an_update()
    {
        using var coordinator = Create();

        await coordinator.ApplyAsync();
        await coordinator.CheckAsync();          // up to date
        await coordinator.ApplyAsync();

        _service.ApplyCalls.Should().Be(0);
    }

    [Fact]
    public async Task Apply_is_refused_while_an_install_is_running_and_says_which_one()
    {
        var plan = await new WizardModuleRegistry(() => [])
            .BuildPlanAsync(new WizardSelection { Workload = new InstallationConfiguration { Name = "Krea-2-Turbo" } });
        _session.SetupGet(s => s.Phase).Returns(InstallPhase.Running);
        _session.SetupGet(s => s.Plan).Returns(plan);
        _service.NextCheck = () => CatalogChecks.Available();
        using var coordinator = Create();
        await coordinator.CheckAsync();

        coordinator.UpdateAvailable.Should().BeTrue();
        coordinator.CanApply.Should().BeFalse();
        coordinator.ApplyBlockedReason.Should().Be("It can be applied once Krea-2-Turbo has finished.");

        await coordinator.ApplyAsync();
        _service.ApplyCalls.Should().Be(0);
    }

    // ----- apply -----

    private async Task<CatalogUpdateCoordinator> CheckedWithUpdateAsync()
    {
        WriteInstalledState(3);
        _service.NextCheck = () => CatalogChecks.Available(4);
        var coordinator = Create();
        await coordinator.CheckAsync();
        return coordinator;
    }

    [Fact]
    public async Task Apply_sends_both_sections_forwards_progress_and_ends_applied()
    {
        _service.HoldApply = new TaskCompletionSource();
        using var coordinator = await CheckedWithUpdateAsync();
        var raised = 0;
        coordinator.Changed += () => raised++;

        var apply = coordinator.ApplyAsync();

        coordinator.Phase.Should().Be(CatalogUpdatePhase.Applying);
        coordinator.CanApply.Should().BeFalse("a second click must not start a second download");
        SpinWait.SpinUntil(() => _service.Progress is not null, 2000).Should().BeTrue();
        _service.AppliedSections.Should().Be(CatalogSections.All);

        _service.Progress!.Report(new CatalogDownloadProgress(50, 100));
        coordinator.Progress.Should().Be(new CatalogDownloadProgress(50, 100));
        var raisedBeforeFinish = raised;
        raisedBeforeFinish.Should().BeGreaterThanOrEqualTo(2, "entering Applying and the progress report");

        WriteInstalledState(4);   // what the SDK's swap leaves on disk
        _service.HoldApply.SetResult();
        await apply;

        coordinator.Phase.Should().Be(CatalogUpdatePhase.Applied);
        coordinator.LastApply.Should().Be(new CatalogApplyResult(CatalogSections.All, CatalogSections.None, null));
        coordinator.Progress.Should().BeNull();
        coordinator.Installed!.HighestCatalogVersion.Should().Be(4);
        coordinator.UpdateAvailable.Should().BeFalse("the dot and the notice go away once it is in");
        coordinator.CanApply.Should().BeFalse();
        raised.Should().BeGreaterThan(raisedBeforeFinish);
    }

    [Fact]
    public async Task A_failed_apply_returns_to_checked_with_the_error_so_it_can_be_retried()
    {
        _service.NextApply = () => new CatalogApplyResult(CatalogSections.None, CatalogSections.All, "sha256 mismatch");
        using var coordinator = await CheckedWithUpdateAsync();

        await coordinator.ApplyAsync();

        coordinator.Phase.Should().Be(CatalogUpdatePhase.Checked);
        coordinator.LastApply!.Error.Should().Be("sha256 mismatch");
        coordinator.UpdateAvailable.Should().BeTrue();
        coordinator.CanApply.Should().BeTrue();
    }

    [Fact]
    public async Task A_second_check_after_a_failed_apply_clears_the_stale_error()
    {
        // LastApply is read directly by the failure line, not gated through LastCheck's outcome --
        // so without CheckAsync clearing it, a fresh check would sit right under a retry banner
        // for an apply nobody has attempted since.
        _service.NextApply = () => new CatalogApplyResult(CatalogSections.None, CatalogSections.All, "sha256 mismatch");
        using var coordinator = await CheckedWithUpdateAsync();
        await coordinator.ApplyAsync();
        coordinator.LastApply!.Error.Should().Be("sha256 mismatch");

        await coordinator.CheckAsync();

        coordinator.LastApply.Should().BeNull();
    }

    [Fact]
    public async Task A_partial_apply_also_returns_to_checked()
    {
        _service.NextApply = () => new CatalogApplyResult(CatalogSections.Workloads, CatalogSections.Workflows, "workflows/ locked");
        using var coordinator = await CheckedWithUpdateAsync();

        await coordinator.ApplyAsync();

        coordinator.Phase.Should().Be(CatalogUpdatePhase.Checked);
        coordinator.LastApply!.Applied.Should().Be(CatalogSections.Workloads);
    }

    [Fact]
    public async Task A_cancelled_apply_returns_to_checked_without_an_error()
    {
        _service.ThrowCancelledOnApply = true;
        using var coordinator = await CheckedWithUpdateAsync();

        await coordinator.ApplyAsync();

        coordinator.Phase.Should().Be(CatalogUpdatePhase.Checked);
        coordinator.LastApply.Should().BeNull();
        coordinator.Progress.Should().BeNull();
    }

    [Fact]
    public async Task A_check_requested_while_applying_is_a_no_op_that_does_not_wait_for_the_download()
    {
        // Handing back the apply's task would leave "Check for updates" stuck on "Checking..."
        // for the whole download -- the same trap ApplyAsync's refusals already avoid.
        _service.HoldApply = new TaskCompletionSource();
        using var coordinator = await CheckedWithUpdateAsync();
        var apply = coordinator.ApplyAsync();

        var check = coordinator.CheckAsync();

        check.IsCompletedSuccessfully.Should().BeTrue();
        coordinator.Phase.Should().Be(CatalogUpdatePhase.Applying, "the refused check must not disturb the apply");
        _service.CheckCalls.Should().Be(1);

        _service.HoldApply.SetResult();
        await apply;
    }

    [Fact]
    public async Task Progress_raises_changed_only_when_the_displayed_value_moves()
    {
        // The SDK reports on every ~80 KB read; the pages show a whole percent (or 0.1 MB without
        // a total), so thousands of reports are at most ~100 distinct renders.
        _service.HoldApply = new TaskCompletionSource();
        using var coordinator = await CheckedWithUpdateAsync();
        var apply = coordinator.ApplyAsync();
        SpinWait.SpinUntil(() => _service.Progress is not null, 2000).Should().BeTrue();
        var raised = 0;
        coordinator.Changed += () => raised++;

        for (var bytes = 1; bytes <= 1000; bytes++)
            _service.Progress!.Report(new CatalogDownloadProgress(bytes, 100_000));   // 0% .. 1%

        raised.Should().BeLessThanOrEqualTo(2);
        coordinator.Progress.Should().Be(new CatalogDownloadProgress(1000, 100_000), "the latest value is always stored");

        raised = 0;
        _service.Progress!.Report(new CatalogDownloadProgress(50_000, 100_000));
        raised.Should().Be(1, "a new percent is a new render");

        raised = 0;
        for (var bytes = 1; bytes <= 50_000; bytes++)
            _service.Progress!.Report(new CatalogDownloadProgress(bytes, null));      // under 0.1 MB
        raised.Should().BeLessThanOrEqualTo(2);

        _service.HoldApply.SetResult();
        await apply;
    }

    [Fact]
    public async Task After_a_successful_apply_a_new_check_can_run()
    {
        using var coordinator = await CheckedWithUpdateAsync();
        await coordinator.ApplyAsync();
        _service.NextCheck = () => CatalogChecks.Outcome(CatalogUpdateOutcome.UpToDate);

        await coordinator.CheckAsync();

        coordinator.Phase.Should().Be(CatalogUpdatePhase.Checked);
        _service.CheckCalls.Should().Be(2);
    }

    // ----- channel switch (#39: preview, warn, switch, fall through) -----

    private static CatalogUpdateCheck StableRemoves(string workload) =>
        CatalogChecks.Available(4, CatalogChannel.Stable, workloads: [CatalogChecks.WorkloadRemoved(workload, "V1.0")]);

    private CatalogUpdateCoordinator CreateOnPreview(int installed = 5)
    {
        _saved.CatalogChannel = "Preview";
        WriteInstalledState(installed);
        return Create();
    }

    private void VerifyNothingSaved() =>
        _settings.Verify(s => s.SaveAsync(It.IsAny<UserSettings>(), It.IsAny<CancellationToken>()), Times.Never);

    private void VerifySaved(string channel) =>
        _settings.Verify(s => s.SaveAsync(It.Is<UserSettings>(u => u.CatalogChannel == channel), It.IsAny<CancellationToken>()), Times.Once);

    [Fact]
    public async Task A_switch_previews_the_target_channel_without_repointing_the_sdk()
    {
        _service.HoldCheck = new TaskCompletionSource();
        using var coordinator = CreateOnPreview();

        var switching = coordinator.SwitchChannelAsync(CatalogChannel.Stable);

        SpinWait.SpinUntil(() => _service.PreviewedChannels.Count > 0, 2000).Should().BeTrue();
        _service.PreviewedChannels.Should().Equal(CatalogChannel.Stable);
        _service.CheckCalls.Should().Be(0, "the configured-channel check reads the option the preview must not touch");
        _options.Channel.Should().Be(CatalogChannel.Preview);
        coordinator.Channel.Should().Be(CatalogChannel.Preview);
        coordinator.Phase.Should().Be(CatalogUpdatePhase.Checking);
        VerifyNothingSaved();

        _service.HoldCheck.SetResult();
        await switching;
    }

    [Fact]
    public async Task A_switch_that_removes_or_changes_something_waits_for_an_answer_and_saves_nothing()
    {
        _service.NextPreview = _ => StableRemoves("Qwen-Image-2.1");
        using var coordinator = CreateOnPreview();
        await coordinator.CheckAsync();
        var before = coordinator.LastCheck;

        await coordinator.SwitchChannelAsync(CatalogChannel.Stable);

        coordinator.PendingSwitch.Should().NotBeNull();
        coordinator.PendingSwitch!.Target.Should().Be(CatalogChannel.Stable);
        coordinator.PendingSwitch.Warning.Removed.Should().Equal("Qwen-Image-2.1");
        VerifyNothingSaved();
        _service.ApplyCalls.Should().Be(0);
        coordinator.Channel.Should().Be(CatalogChannel.Preview);
        _options.Channel.Should().Be(CatalogChannel.Preview);
        coordinator.LastCheck.Should().BeSameAs(before, "what was on the page stays until the user answers");
        coordinator.Phase.Should().Be(CatalogUpdatePhase.Checked);
    }

    [Fact]
    public async Task A_changed_workflow_needs_an_answer_too()
    {
        _service.NextPreview = _ => CatalogChecks.Available(4, CatalogChannel.Stable, workloads: [],
            workflows: [CatalogChecks.WorkflowUpdated("Upscale", "V2", "V1", "Krea-2-Turbo")]);
        using var coordinator = CreateOnPreview();

        await coordinator.SwitchChannelAsync(CatalogChannel.Stable);

        coordinator.PendingSwitch.Should().NotBeNull();
        _service.ApplyCalls.Should().Be(0);
    }

    [Fact]
    public async Task Switching_saves_the_preference_then_applies_the_previewed_check()
    {
        _service.NextPreview = _ => StableRemoves("Qwen-Image-2.1");
        var appliesAtSave = -1;
        _settings.Setup(s => s.SaveAsync(It.IsAny<UserSettings>(), It.IsAny<CancellationToken>()))
                 .ReturnsAsync((UserSettings s, CancellationToken _) => { appliesAtSave = _service.ApplyCalls; return s; });
        using var coordinator = CreateOnPreview();
        await coordinator.SwitchChannelAsync(CatalogChannel.Stable);
        var preview = coordinator.PendingSwitch!.Preview;
        _service.OnApply = () => WriteInstalledState(4);

        await coordinator.ConfirmSwitchAsync();

        VerifySaved("Stable");
        appliesAtSave.Should().Be(0, "the preference is saved before the download starts, so a failed download cannot lose the choice");
        _service.AppliedCheck.Should().BeSameAs(preview, "the apply downloads from and stamps the channel the preview read");
        _service.AppliedSections.Should().Be(CatalogSections.All);
        _service.CheckCalls.Should().Be(0, "no second check: the preview is what the user agreed to");
        coordinator.Channel.Should().Be(CatalogChannel.Stable);
        _options.Channel.Should().Be(CatalogChannel.Stable);
        coordinator.PendingSwitch.Should().BeNull();
        coordinator.Phase.Should().Be(CatalogUpdatePhase.Applied);
        coordinator.Installed!.HighestCatalogVersion.Should().Be(4);
        coordinator.SwitchIncomplete.Should().BeFalse();
    }

    [Fact]
    public async Task Keep_saves_nothing_and_leaves_everything_as_it_was()
    {
        _service.NextPreview = _ => StableRemoves("Qwen-Image-2.1");
        using var coordinator = CreateOnPreview();
        await coordinator.CheckAsync();
        var before = coordinator.LastCheck;
        await coordinator.SwitchChannelAsync(CatalogChannel.Stable);
        var raised = 0;
        coordinator.Changed += () => raised++;

        coordinator.KeepChannel();

        coordinator.PendingSwitch.Should().BeNull();
        VerifyNothingSaved();
        _service.ApplyCalls.Should().Be(0);
        coordinator.Channel.Should().Be(CatalogChannel.Preview);
        coordinator.LastCheck.Should().BeSameAs(before);
        coordinator.SwitchIncomplete.Should().BeFalse();
        raised.Should().Be(1, "the radio snaps back on that render");

        await coordinator.CheckAsync();
        _service.CheckCalls.Should().Be(2, "Keep ends the switch, so checks run again");
    }

    [Fact]
    public async Task A_switch_that_only_adds_applies_without_asking()
    {
        _service.NextPreview = _ => CatalogChecks.Available(6, CatalogChannel.Stable, workloads: [CatalogChecks.WorkloadAdded("Wan-2.3", "V1.0")]);
        using var coordinator = CreateOnPreview();

        await coordinator.SwitchChannelAsync(CatalogChannel.Stable);

        coordinator.PendingSwitch.Should().BeNull();
        VerifySaved("Stable");
        _service.ApplyCalls.Should().Be(1);
        coordinator.Phase.Should().Be(CatalogUpdatePhase.Applied);
    }

    [Fact]
    public async Task A_switch_to_the_same_content_only_saves_the_preference()
    {
        _service.NextPreview = channel => CatalogChecks.Outcome(CatalogUpdateOutcome.UpToDate) with { Channel = channel };
        using var coordinator = CreateOnPreview();

        await coordinator.SwitchChannelAsync(CatalogChannel.Stable);

        coordinator.PendingSwitch.Should().BeNull();
        VerifySaved("Stable");
        _service.ApplyCalls.Should().Be(0);
        coordinator.Channel.Should().Be(CatalogChannel.Stable);
        coordinator.Phase.Should().Be(CatalogUpdatePhase.Checked);
        coordinator.LastCheck!.Outcome.Should().Be(CatalogUpdateOutcome.UpToDate);
        coordinator.LastCheck.Channel.Should().Be(CatalogChannel.Stable);
        coordinator.SwitchIncomplete.Should().BeFalse();
    }

    [Fact]
    public async Task A_failed_preview_still_saves_the_preference_and_keeps_the_installed_content()
    {
        _service.NextPreview = channel => CatalogChecks.Outcome(CatalogUpdateOutcome.Failed, "No such host is known.") with { Channel = channel };
        using var coordinator = CreateOnPreview();

        await coordinator.SwitchChannelAsync(CatalogChannel.Stable);

        VerifySaved("Stable");
        coordinator.Channel.Should().Be(CatalogChannel.Stable);
        _service.ApplyCalls.Should().Be(0);
        coordinator.PendingSwitch.Should().BeNull();
        coordinator.SwitchIncomplete.Should().BeTrue();
        coordinator.Installed!.HighestCatalogVersion.Should().Be(5);
        coordinator.Phase.Should().Be(CatalogUpdatePhase.Checked);
    }

    [Fact]
    public async Task A_preview_that_throws_is_a_failed_preview_not_a_crash()
    {
        _service.NextPreview = _ => throw new InvalidOperationException("boom");
        using var coordinator = CreateOnPreview();

        await coordinator.SwitchChannelAsync(CatalogChannel.Stable);

        VerifySaved("Stable");
        coordinator.LastCheck!.Outcome.Should().Be(CatalogUpdateOutcome.Failed);
        coordinator.LastCheck.Error.Should().Be("boom");
        coordinator.SwitchIncomplete.Should().BeTrue();
    }

    [Fact]
    public async Task A_failed_apply_still_saves_the_preference_and_can_be_retried()
    {
        _service.NextPreview = _ => StableRemoves("Qwen-Image-2.1");
        _service.NextApply = () => new CatalogApplyResult(CatalogSections.None, CatalogSections.All, "The download stalled.");
        using var coordinator = CreateOnPreview();
        await coordinator.SwitchChannelAsync(CatalogChannel.Stable);

        await coordinator.ConfirmSwitchAsync();

        VerifySaved("Stable");
        coordinator.Channel.Should().Be(CatalogChannel.Stable);
        coordinator.SwitchIncomplete.Should().BeTrue();
        coordinator.Phase.Should().Be(CatalogUpdatePhase.Checked);
        coordinator.CanApply.Should().BeTrue("the Apply button is the retry");

        _service.NextApply = () => new CatalogApplyResult(CatalogSections.All, CatalogSections.None, null);
        await coordinator.ApplyAsync();

        coordinator.SwitchIncomplete.Should().BeFalse();
        _service.AppliedCheck!.Channel.Should().Be(CatalogChannel.Stable);
    }

    [Fact]
    public async Task A_check_that_finds_the_catalog_current_ends_an_incomplete_switch()
    {
        _service.NextPreview = channel => CatalogChecks.Outcome(CatalogUpdateOutcome.Failed, "offline") with { Channel = channel };
        using var coordinator = CreateOnPreview();
        await coordinator.SwitchChannelAsync(CatalogChannel.Stable);

        _service.NextCheck = () => CatalogChecks.Outcome(CatalogUpdateOutcome.Failed, "still offline");
        await coordinator.CheckAsync();
        coordinator.SwitchIncomplete.Should().BeTrue("a retry that fails again changes nothing");

        _service.NextCheck = () => CatalogChecks.Outcome(CatalogUpdateOutcome.UpToDate);
        await coordinator.CheckAsync();
        coordinator.SwitchIncomplete.Should().BeFalse();
    }

    [Fact]
    public async Task A_switch_during_an_install_saves_the_preference_and_holds_the_apply()
    {
        var plan = await new WizardModuleRegistry(() => [])
            .BuildPlanAsync(new WizardSelection { Workload = new InstallationConfiguration { Name = "Krea-2-Turbo" } });
        _session.SetupGet(s => s.Phase).Returns(InstallPhase.Running);
        _session.SetupGet(s => s.Plan).Returns(plan);
        _service.NextPreview = _ => StableRemoves("Qwen-Image-2.1");
        using var coordinator = CreateOnPreview();
        await coordinator.SwitchChannelAsync(CatalogChannel.Stable);

        await coordinator.ConfirmSwitchAsync();

        VerifySaved("Stable");
        _service.ApplyCalls.Should().Be(0, "never swap content under a running plan");
        coordinator.ApplyBlockedReason.Should().Be("It can be applied once Krea-2-Turbo has finished.");
        coordinator.SwitchIncomplete.Should().BeTrue();

        _session.SetupGet(s => s.Phase).Returns(InstallPhase.Completed);
        await coordinator.ApplyAsync();
        _service.ApplyCalls.Should().Be(1);
    }

    [Fact]
    public async Task Checks_applies_and_other_switches_are_refused_while_a_switch_waits_for_an_answer()
    {
        _service.NextPreview = _ => StableRemoves("Qwen-Image-2.1");
        using var coordinator = CreateOnPreview();
        await coordinator.SwitchChannelAsync(CatalogChannel.Stable);
        var pending = coordinator.PendingSwitch;

        await coordinator.CheckAsync();
        await coordinator.ApplyAsync();
        await coordinator.SwitchChannelAsync(CatalogChannel.Stable);

        _service.CheckCalls.Should().Be(0);
        _service.ApplyCalls.Should().Be(0);
        _service.PreviewedChannels.Should().HaveCount(1);
        coordinator.PendingSwitch.Should().BeSameAs(pending);
    }

    [Fact]
    public async Task Confirming_or_keeping_without_a_pending_switch_does_nothing()
    {
        using var coordinator = CreateOnPreview();

        await coordinator.ConfirmSwitchAsync();
        coordinator.KeepChannel();

        VerifyNothingSaved();
        _service.ApplyCalls.Should().Be(0);
        coordinator.Phase.Should().Be(CatalogUpdatePhase.Idle);
    }

    [Fact]
    public async Task A_failed_save_on_switch_changes_nothing_and_propagates()
    {
        _service.NextPreview = _ => StableRemoves("Qwen-Image-2.1");
        _settings.Setup(s => s.SaveAsync(It.IsAny<UserSettings>(), It.IsAny<CancellationToken>())).ThrowsAsync(new IOException("settings.json is locked"));
        using var coordinator = CreateOnPreview();
        await coordinator.SwitchChannelAsync(CatalogChannel.Stable);

        var confirm = () => coordinator.ConfirmSwitchAsync();

        await confirm.Should().ThrowAsync<IOException>();
        coordinator.Channel.Should().Be(CatalogChannel.Preview);
        _service.ApplyCalls.Should().Be(0);
        coordinator.PendingSwitch.Should().BeNull();
        await coordinator.CheckAsync();
        _service.CheckCalls.Should().Be(1, "a failed save must not leave the coordinator stuck in a switch");
    }

    [Fact]
    public async Task Switching_to_the_channel_already_followed_does_nothing()
    {
        using var coordinator = CreateOnPreview();

        await coordinator.SwitchChannelAsync(CatalogChannel.Preview);

        _service.PreviewedChannels.Should().BeEmpty();
        VerifyNothingSaved();
    }

    [Fact]
    public async Task A_switch_under_an_environment_override_saves_but_previews_and_applies_nothing()
    {
        _environment = "stable";
        using var coordinator = Create();

        await coordinator.SwitchChannelAsync(CatalogChannel.Preview);

        _saved.CatalogChannel.Should().Be("Preview");
        (coordinator.Channel, coordinator.ChannelSource).Should().Be((CatalogChannel.Stable, CatalogChannelSource.Environment));
        _service.PreviewedChannels.Should().BeEmpty("the environment decides what is installed; previewing another channel would be a lie");
        _service.ApplyCalls.Should().Be(0);
    }

    [Fact]
    public async Task A_switch_is_refused_while_a_check_is_running()
    {
        _service.HoldCheck = new TaskCompletionSource();
        using var coordinator = Create();
        var check = coordinator.CheckAsync();

        await coordinator.SwitchChannelAsync(CatalogChannel.Preview);

        VerifyNothingSaved();
        _service.PreviewedChannels.Should().BeEmpty();
        _service.HoldCheck.SetResult();
        await check;
    }

    // ----- session link -----

    [Fact]
    public void An_install_starting_or_finishing_re_raises_changed_once_per_flip()
    {
        using var coordinator = Create();
        var raised = 0;
        coordinator.Changed += () => raised++;

        _session.SetupGet(s => s.Phase).Returns(InstallPhase.Running);
        _session.Raise(s => s.Changed += null);
        _session.Raise(s => s.Changed += null);   // progress ticks: same state, no re-raise
        raised.Should().Be(1);

        _session.SetupGet(s => s.Phase).Returns(InstallPhase.Completed);
        _session.Raise(s => s.Changed += null);
        raised.Should().Be(2);
    }

    [Fact]
    public void Disposing_unsubscribes_from_the_session()
    {
        var coordinator = Create();
        coordinator.Dispose();
        _session.VerifyRemove(s => s.Changed -= It.IsAny<Action>(), Times.Once);
    }

    // ----- review fixes (task 4 re-review) -----

    [Fact]
    public async Task A_throwing_Changed_subscriber_does_not_fault_the_check()
    {
        using var coordinator = Create();
        var secondRan = false;
        coordinator.Changed += () => throw new InvalidOperationException("boom");
        coordinator.Changed += () => secondRan = true;

        await coordinator.CheckAsync();

        secondRan.Should().BeTrue();
        coordinator.Phase.Should().Be(CatalogUpdatePhase.Checked);
    }

    [Fact]
    public async Task A_check_started_during_a_channel_switch_is_refused_until_the_switch_finishes()
    {
        var saveGate = new TaskCompletionSource();
        _settings.Setup(s => s.SaveAsync(It.IsAny<UserSettings>(), It.IsAny<CancellationToken>()))
                 .Returns(async (UserSettings s, CancellationToken _) => { await saveGate.Task; return s; });
        using var coordinator = Create();

        var switching = coordinator.SwitchChannelAsync(CatalogChannel.Preview);
        await coordinator.CheckAsync();

        _service.CheckCalls.Should().Be(0);
        coordinator.Phase.Should().Be(CatalogUpdatePhase.Idle);

        saveGate.SetResult();
        await switching;

        await coordinator.CheckAsync();

        _service.CheckCalls.Should().Be(1);
        _options.Channel.Should().Be(CatalogChannel.Preview);
    }

    [Fact]
    public async Task A_transient_settings_read_failure_does_not_permanently_pin_the_channel_to_stable()
    {
        _settings.SetupSequence(s => s.GetOrCreateForCurrentUserAsync(It.IsAny<CancellationToken>()))
                 .ThrowsAsync(new IOException("locked"))
                 .ReturnsAsync(new UserSettings { UserName = "tester", CatalogChannel = "Preview" });
        using var coordinator = Create();

        await coordinator.CheckAsync();
        coordinator.ChannelSource.Should().Be(CatalogChannelSource.Default);

        await coordinator.CheckAsync();

        coordinator.Channel.Should().Be(CatalogChannel.Preview);
        coordinator.ChannelSource.Should().Be(CatalogChannelSource.Setting);
    }
}
