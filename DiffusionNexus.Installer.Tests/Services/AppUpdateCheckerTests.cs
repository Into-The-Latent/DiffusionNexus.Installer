using DiffusionNexus.Installer.Electron.Services;
using DiffusionNexus.Installer.SDK.Catalog.Packaging;
using DiffusionNexus.Installer.Tests.Support;
using FluentAssertions;
using Xunit;

namespace DiffusionNexus.Installer.Tests.Services;

/// <summary>
/// The app follows the same Stable/Preview setting as the catalog (issue #19). A Preview catalog
/// on a Stable app is exactly the "needs a newer installer" outcome the catalog check reports, so
/// the app check must never run on a channel other than the one the catalog is following.
/// </summary>
public class AppUpdateCheckerTests
{
    private readonly StubCatalogUpdateCoordinator _catalog = new();
    private readonly FakeAppUpdaterShell _shell = new();
    private readonly UpdaterLog _log = new();

    private AppUpdateChecker Create() => new(_catalog, _shell, _log);

    [Fact]
    public async Task Preview_allows_pre_releases_before_the_check_runs()
    {
        _catalog.Channel = CatalogChannel.Preview;

        await Create().CheckAsync();

        _shell.Calls.Should().Equal(["allowPrerelease=True", "check"]);
    }

    [Fact]
    public async Task Stable_turns_pre_releases_off_before_the_check_runs()
    {
        _catalog.Channel = CatalogChannel.Stable;

        await Create().CheckAsync();

        _shell.Calls.Should().Equal(["allowPrerelease=False", "check"]);
    }

    // The updater lives in Electron's main process and keeps whatever it was last told, so a
    // switch back to Stable has to be sent, not assumed.
    [Fact]
    public async Task A_channel_switch_between_checks_is_sent_to_the_updater_again()
    {
        var checker = Create();
        _catalog.Channel = CatalogChannel.Preview;
        await checker.CheckAsync();

        _catalog.Channel = CatalogChannel.Stable;
        await checker.CheckAsync();

        _shell.Calls.Should().Equal(["allowPrerelease=True", "check", "allowPrerelease=False", "check"]);
    }

    [Fact]
    public async Task The_channel_is_resolved_first_so_a_startup_check_does_not_race_the_catalog_check()
    {
        await Create().CheckAsync();

        _catalog.ChannelResolutions.Should().Be(1);
    }

    [Fact]
    public async Task Outside_electron_it_does_nothing()
    {
        _shell.IsAvailable = false;

        await Create().CheckAsync();

        _shell.Calls.Should().BeEmpty();
        _log.Lines.Should().BeEmpty();
    }

    [Fact]
    public async Task A_failing_check_is_logged_and_never_thrown()
    {
        _shell.CheckFailure = new InvalidOperationException("net::ERR_INTERNET_DISCONNECTED");

        await Create().Invoking(c => c.CheckAsync()).Should().NotThrowAsync();

        _log.Lines.Should().ContainSingle(l => l.Contains("net::ERR_INTERNET_DISCONNECTED"));
    }

    [Fact]
    public async Task The_log_names_the_channel_in_our_words()
    {
        _catalog.Channel = CatalogChannel.Preview;

        await Create().CheckAsync();

        _log.Lines.Should().Contain(l => l.Contains("Preview"));
        _log.Lines.Should().NotContain(l => l.Contains("beta") || l.Contains("prerelease"));
    }
}
