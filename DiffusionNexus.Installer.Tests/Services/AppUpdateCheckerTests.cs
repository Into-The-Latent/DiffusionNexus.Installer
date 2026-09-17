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
public class AppUpdateCheckerTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"dn-app-updates-{Guid.NewGuid():N}");
    private readonly StubCatalogUpdateCoordinator _catalog = new();
    private readonly FakeAppUpdaterShell _shell = new();
    private readonly FakeFeed _feed = new();
    private readonly UpdaterLog _log = new();

    public AppUpdateCheckerTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    private string PinnedPath => Path.Combine(_dir, "pinned.yml");

    private AppUpdateChecker Create() => new(_catalog, _shell, _log, _feed, PinnedPath);

    /// <summary>An installed app: electron-builder's app-update.yml is on disk next to it.</summary>
    private string Installed()
    {
        var shipped = Path.Combine(_dir, "app-update.yml");
        File.WriteAllText(shipped, "owner: Into-The-Latent\nrepo: DiffusionNexus.Installer\nprovider: github\nupdaterCacheDirName: dn-updater\n");
        _shell.UpdateConfigPath = shipped;
        return shipped;
    }

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
        _log.Status.Should().Be(AppUpdateStatus.Unavailable);
    }

    [Fact]
    public async Task A_failing_check_is_logged_and_never_thrown()
    {
        _shell.CheckFailure = new InvalidOperationException("net::ERR_INTERNET_DISCONNECTED");

        await Create().Invoking(c => c.CheckAsync()).Should().NotThrowAsync();

        _log.Lines.Should().ContainSingle(l => l.Contains("net::ERR_INTERNET_DISCONNECTED"));
    }

    // ----- Preview means the highest version, not the newest-created release (PR #21 review) -----

    // electron-updater's Preview path takes the first feed entry. A Stable hotfix cut after a
    // pre-release is that first entry, and would hide the pre-release from every tester.
    [Fact]
    public async Task A_stable_hotfix_published_after_a_pre_release_does_not_hide_it_from_preview()
    {
        Installed();
        _catalog.Channel = CatalogChannel.Preview;
        _feed.Tags = ["v3.0.8", "v3.1.0", "v3.0.7"];

        await Create().CheckAsync();

        _shell.Calls.Should().Equal(["allowPrerelease=True", $"configPath={PinnedPath}", "check"]);
        File.ReadAllText(PinnedPath).Should().Contain("releases/download/v3.1.0").And.Contain("updaterCacheDirName: 'dn-updater'");
        _feed.UrlsRead.Should().Equal(["https://github.com/Into-The-Latent/DiffusionNexus.Installer/releases.atom"]);
    }

    // The common case stays on electron-updater's own GitHub path, which also keeps its
    // differential downloads; the pin is only for the case that path gets wrong.
    [Fact]
    public async Task When_the_newest_release_is_also_the_highest_the_updater_is_left_alone()
    {
        Installed();
        _catalog.Channel = CatalogChannel.Preview;
        _feed.Tags = ["v3.1.0", "v3.0.8"];

        await Create().CheckAsync();

        _shell.Calls.Should().Equal(["allowPrerelease=True", "check"]);
    }

    [Fact]
    public async Task An_unreadable_feed_falls_back_to_the_plain_check()
    {
        Installed();
        _catalog.Channel = CatalogChannel.Preview;
        _feed.Failure = new HttpRequestException("offline");

        await Create().Invoking(c => c.CheckAsync()).Should().NotThrowAsync();

        _shell.Calls.Should().Equal(["allowPrerelease=True", "check"]);
    }

    // Setting the path goes through ElectronNET internals (see ElectronAppUpdaterShell). If that
    // throws, the check still runs, and the next one puts the shipped config back in case the
    // message did land.
    [Fact]
    public async Task An_updater_that_refuses_the_pin_still_checks_and_is_reset_next_time()
    {
        var shipped = Installed();
        _catalog.Channel = CatalogChannel.Preview;
        _feed.Tags = ["v3.0.8", "v3.1.0"];
        _shell.ConfigPathFailure = new InvalidOperationException("bridge socket was not found");
        var checker = Create();

        await checker.CheckAsync();

        _shell.Calls.Should().Contain("check");
        _log.Lines.Should().Contain(l => l.Contains("bridge socket was not found"));

        _shell.ConfigPathFailure = null;
        _feed.Tags = ["v3.1.0", "v3.0.8"];
        _shell.Calls.Clear();
        await checker.CheckAsync();

        _shell.Calls.Should().Equal(["allowPrerelease=True", $"configPath={shipped}", "check"]);
    }

    // The reflection target, resolved against the ElectronNET package this build references: a
    // bump that renames it fails here, not in a tester's update check.
    [Fact]
    public void The_electronnet_bridge_the_pin_is_sent_over_still_exists()
    {
        ElectronAppUpdaterShell.BridgeEmit().Should().NotBeNull();
    }

    [Fact]
    public async Task Stable_never_reads_the_feed()
    {
        Installed();

        await Create().CheckAsync();

        _feed.UrlsRead.Should().BeEmpty();
        _shell.Calls.Should().Equal(["allowPrerelease=False", "check"]);
    }

    // A pinned updater only ever sees one release, so leaving it pinned would freeze Stable.
    [Fact]
    public async Task Switching_to_stable_after_a_pin_puts_the_shipped_config_back()
    {
        var shipped = Installed();
        _catalog.Channel = CatalogChannel.Preview;
        _feed.Tags = ["v3.0.8", "v3.1.0"];
        var checker = Create();
        await checker.CheckAsync();
        _shell.Calls.Clear();

        _catalog.Channel = CatalogChannel.Stable;
        await checker.CheckAsync();

        _shell.Calls.Should().Equal(["allowPrerelease=False", $"configPath={shipped}", "check"]);
    }

    [Fact]
    public async Task A_pin_is_lifted_once_the_feed_is_back_in_order()
    {
        var shipped = Installed();
        _catalog.Channel = CatalogChannel.Preview;
        _feed.Tags = ["v3.0.8", "v3.1.0"];
        var checker = Create();
        await checker.CheckAsync();
        _shell.Calls.Clear();

        _feed.Tags = ["v3.1.1", "v3.0.8", "v3.1.0"];
        await checker.CheckAsync();

        _shell.Calls.Should().Equal(["allowPrerelease=True", $"configPath={shipped}", "check"]);
    }

    // `dotnet run` inside Electron: no packaged app, so no app-update.yml to learn the repo from.
    [Fact]
    public async Task Without_a_shipped_config_the_feed_is_not_read()
    {
        _catalog.Channel = CatalogChannel.Preview;

        await Create().CheckAsync();

        _feed.UrlsRead.Should().BeEmpty();
        _shell.Calls.Should().Equal(["allowPrerelease=True", "check"]);
    }

    // Inside Electron but unpackaged, electron-updater returns without firing one event. The check
    // still goes out (a packaged app whose config we failed to find must not lose its updates),
    // but the page says why nothing came back instead of looking hung.
    [Fact]
    public async Task Without_a_shipped_config_it_says_the_build_is_not_installed()
    {
        await Create().CheckAsync();

        _log.Status.Should().Be(AppUpdateStatus.NotInstalledBuild);
        _log.Lines.Should().Contain("This build is not installed, so there is no app update to check for.");
        _shell.Calls.Should().EndWith("check");
    }

    [Fact]
    public async Task An_installed_build_is_not_called_uninstalled()
    {
        Installed();

        await Create().CheckAsync();

        _log.Status.Should().NotBe(AppUpdateStatus.NotInstalledBuild);
    }

    // The startup check and every button press used to log "Following Stable app releases."
    // again, so the log filled with the same line.
    [Fact]
    public async Task The_channel_is_logged_once_until_it_changes()
    {
        Installed();
        var checker = Create();

        await checker.CheckAsync();
        await checker.CheckAsync();
        _catalog.Channel = CatalogChannel.Preview;
        await checker.CheckAsync();

        _log.Lines.Where(l => l.StartsWith("Following", StringComparison.Ordinal))
            .Should().Equal("Following Stable app releases.", "Following Preview app releases.");
    }

    [Fact]
    public async Task The_log_names_the_channel_in_our_words()
    {
        _catalog.Channel = CatalogChannel.Preview;

        await Create().CheckAsync();

        _log.Lines.Should().Contain(l => l.Contains("Preview"));
        _log.Lines.Should().NotContain(l => l.Contains("beta") || l.Contains("prerelease"));
    }

    private sealed class FakeFeed : IAppReleaseFeed
    {
        public string[] Tags { get; set; } = [];
        public Exception? Failure { get; set; }
        public List<string> UrlsRead { get; } = [];

        public Task<string> ReadAsync(string url, CancellationToken ct = default)
        {
            UrlsRead.Add(url);
            if (Failure is not null) return Task.FromException<string>(Failure);
            return Task.FromResult(
                "<feed xmlns=\"http://www.w3.org/2005/Atom\">"
                + string.Concat(Tags.Select(t => $"<entry><link rel=\"alternate\" href=\"https://github.com/o/r/releases/tag/{t}\"/></entry>"))
                + "</feed>");
        }
    }
}
