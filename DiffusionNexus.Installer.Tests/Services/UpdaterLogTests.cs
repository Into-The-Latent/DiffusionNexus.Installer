using DiffusionNexus.Installer.Electron.Services;
using FluentAssertions;
using Xunit;

namespace DiffusionNexus.Installer.Tests.Services;

/// <summary>
/// The app updater's state, as the top bar and /updates read it. Electron's updater events are
/// wired into these methods once in Program.cs, so each one is what the user sees after that event.
/// </summary>
public class UpdaterLogTests
{
    private readonly UpdaterLog _log = new();

    [Fact]
    public void Starts_not_checked_with_nothing_waiting()
    {
        _log.Status.Should().Be(AppUpdateStatus.NotChecked);
        _log.UpdateAvailable.Should().BeFalse();
        _log.UpdateReady.Should().BeFalse();
    }

    [Fact]
    public void An_update_found_is_available_before_it_has_downloaded()
    {
        // The whole point of the top bar change: the user hears about an update when it is found,
        // not only minutes later when the download has finished.
        _log.MarkAvailable("3.0.8");

        _log.Status.Should().Be(AppUpdateStatus.Downloading);
        _log.Version.Should().Be("3.0.8");
        _log.UpdateAvailable.Should().BeTrue();
        _log.UpdateReady.Should().BeFalse();
        _log.Lines.Should().Equal("Update available: 3.0.8. Downloading...");
    }

    [Fact]
    public void Download_progress_is_kept_as_a_whole_percent()
    {
        _log.MarkAvailable("3.0.8");

        _log.MarkProgress(41.6);

        _log.DownloadPercent.Should().Be(42);
    }

    [Fact]
    public void A_downloaded_update_is_ready_and_still_available()
    {
        _log.MarkAvailable("3.0.8");

        _log.MarkUpdateReady("3.0.8");

        _log.Status.Should().Be(AppUpdateStatus.Ready);
        _log.UpdateReady.Should().BeTrue();
        _log.UpdateAvailable.Should().BeTrue();
        _log.Lines.Should().EndWith("Update 3.0.8 downloaded and ready to install.");
    }

    [Fact]
    public void Up_to_date_clears_nothing_waiting()
    {
        _log.MarkChecking();
        _log.Status.Should().Be(AppUpdateStatus.Checking);

        _log.MarkUpToDate();

        _log.Status.Should().Be(AppUpdateStatus.UpToDate);
        _log.UpdateAvailable.Should().BeFalse();
        _log.Lines.Should().Equal("Checking for updates...", "No update available - this is the latest version.");
    }

    [Fact]
    public void A_failure_keeps_its_message()
    {
        _log.MarkFailed("net::ERR_INTERNET_DISCONNECTED");

        _log.Status.Should().Be(AppUpdateStatus.Failed);
        _log.Error.Should().Be("net::ERR_INTERNET_DISCONNECTED");
        _log.Lines.Should().Equal("Updater error: net::ERR_INTERNET_DISCONNECTED");
    }

    // A re-check fires "checking" again. Once a download is on disk that must not hide it: the
    // restart button would vanish for an update that is still waiting to be installed.
    [Fact]
    public void A_later_check_does_not_hide_an_update_that_is_ready()
    {
        _log.MarkUpdateReady("3.0.8");

        _log.MarkChecking();
        _log.MarkUpToDate();

        _log.Status.Should().Be(AppUpdateStatus.Ready);
        _log.UpdateReady.Should().BeTrue();
    }

    // Outside Electron there is no updater at all; the page header already says so, so no line.
    [Fact]
    public void Unavailable_is_a_state_not_a_log_line()
    {
        _log.MarkUnavailable();

        _log.Status.Should().Be(AppUpdateStatus.Unavailable);
        _log.Lines.Should().BeEmpty();
    }

    // electron-updater skips an unpackaged app without firing a single event, which left the log
    // ending at "Following Stable app releases." as if the check had hung.
    [Fact]
    public void A_build_that_is_not_installed_says_why_nothing_was_checked()
    {
        _log.MarkNotInstalledBuild();

        _log.Status.Should().Be(AppUpdateStatus.NotInstalledBuild);
        _log.Lines.Should().Equal("This build is not installed, so there is no app update to check for.");
    }

    [Fact]
    public void The_not_installed_line_is_written_once()
    {
        _log.MarkNotInstalledBuild();
        _log.MarkNotInstalledBuild();

        _log.Lines.Should().ContainSingle();
    }

    [Fact]
    public void Every_state_change_raises_changed()
    {
        var raised = 0;
        _log.Changed += () => raised++;

        _log.MarkChecking();
        _log.MarkAvailable("3.0.8");
        _log.MarkProgress(10);
        _log.MarkUpdateReady("3.0.8");
        _log.MarkFailed("x");
        _log.MarkUnavailable();
        _log.MarkNotInstalledBuild();

        // Append raises it too, so a mark that also writes a line may raise it twice; what matters
        // is that none of them is silent.
        raised.Should().BeGreaterThanOrEqualTo(7);
    }
}
