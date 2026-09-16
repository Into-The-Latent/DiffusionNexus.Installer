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

    private static int Count(ICatalogUpdateCoordinator c) { var n = 0; c.Changed += () => n++; return n; }

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

    // ----- channel switch -----

    [Fact]
    public async Task Setting_the_channel_saves_it_repoints_the_sdk_and_forgets_the_last_check()
    {
        _service.NextCheck = () => CatalogChecks.Available();
        using var coordinator = Create();
        await coordinator.CheckAsync();

        await coordinator.SetChannelAsync(CatalogChannel.Preview);

        _settings.Verify(s => s.SaveAsync(It.Is<UserSettings>(u => u.CatalogChannel == "Preview"), It.IsAny<CancellationToken>()), Times.Once);
        coordinator.Channel.Should().Be(CatalogChannel.Preview);
        coordinator.ChannelSource.Should().Be(CatalogChannelSource.Setting);
        _options.Channel.Should().Be(CatalogChannel.Preview);
        coordinator.LastCheck.Should().BeNull();
        coordinator.UpdateAvailable.Should().BeFalse();
        coordinator.Phase.Should().Be(CatalogUpdatePhase.Idle);
    }

    [Fact]
    public async Task Setting_the_channel_under_an_environment_override_saves_but_keeps_following_the_environment()
    {
        _environment = "stable";
        using var coordinator = Create();

        await coordinator.SetChannelAsync(CatalogChannel.Preview);

        _saved.CatalogChannel.Should().Be("Preview");
        (coordinator.Channel, coordinator.ChannelSource).Should().Be((CatalogChannel.Stable, CatalogChannelSource.Environment));
    }

    [Fact]
    public async Task Setting_the_channel_is_refused_while_a_check_is_running()
    {
        _service.HoldCheck = new TaskCompletionSource();
        using var coordinator = Create();
        var check = coordinator.CheckAsync();

        await coordinator.SetChannelAsync(CatalogChannel.Preview);

        _settings.Verify(s => s.SaveAsync(It.IsAny<UserSettings>(), It.IsAny<CancellationToken>()), Times.Never);
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

        var switching = coordinator.SetChannelAsync(CatalogChannel.Preview);
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
