using DiffusionNexus.Installer.Electron.Services;
using FluentAssertions;
using Xunit;

namespace DiffusionNexus.Installer.Tests.Services;

/// <summary>
/// electron-updater's Preview path takes the FIRST entry of releases.atom -- the most recently
/// created release, not the highest version (PR #21 review). These are the pure halves of the
/// correction: which tag is first, which is highest, and the config that pins the updater to one.
/// </summary>
public class AppReleaseFeedTests
{
    private static string Feed(params string[] tags) =>
        """<?xml version="1.0" encoding="UTF-8"?><feed xmlns="http://www.w3.org/2005/Atom" xmlns:media="http://search.yahoo.com/mrss/">"""
        + """<link type="text/html" rel="alternate" href="https://github.com/Into-The-Latent/DiffusionNexus.Installer/releases"/>"""
        + string.Concat(tags.Select(t =>
            $"""<entry><id>x/{t}</id><link rel="alternate" type="text/html" href="https://github.com/Into-The-Latent/DiffusionNexus.Installer/releases/tag/{t}"/><title>{t}</title></entry>"""))
        + "</feed>";

    [Fact]
    public void A_stable_hotfix_published_after_a_pre_release_is_first_but_not_highest()
    {
        var tags = AppReleaseTags.Parse(Feed("v3.0.8", "v3.1.0", "v3.0.7"));

        tags.First.Should().Be("v3.0.8");
        tags.Highest.Should().Be("v3.1.0");
    }

    [Fact]
    public void Versions_compare_as_numbers_not_text()
    {
        AppReleaseTags.Parse(Feed("v3.0.9", "v3.0.10")).Highest.Should().Be("v3.0.10");
    }

    [Fact]
    public void Tags_that_are_not_a_plain_version_are_never_the_highest()
    {
        var tags = AppReleaseTags.Parse(Feed("nightly", "v9.0.0-beta.1", "v3.0.7"));

        tags.First.Should().Be("nightly");
        tags.Highest.Should().Be("v3.0.7");
    }

    [Fact]
    public void An_empty_feed_has_neither()
    {
        AppReleaseTags.Parse(Feed()).Should().Be(new AppReleaseTags(null, null));
    }

    [Fact]
    public void The_shipped_config_yields_the_repository_and_the_cache_folder()
    {
        var config = AppUpdateConfig.TryParse(
            "owner: Into-The-Latent\nrepo: DiffusionNexus.Installer\nprovider: github\nreleaseType: release\nupdaterCacheDirName: diffusion-nexus-installer-updater\n");

        config.Should().Be(new AppUpdateConfig("Into-The-Latent", "DiffusionNexus.Installer", "diffusion-nexus-installer-updater"));
        config!.FeedUrl.Should().Be("https://github.com/Into-The-Latent/DiffusionNexus.Installer/releases.atom");
    }

    [Theory]
    [InlineData("provider: generic\nurl: https://updater.example\n")]
    [InlineData("owner: someone\nprovider: github\n")]
    [InlineData("")]
    public void A_config_that_is_not_a_complete_github_one_is_refused(string yaml)
    {
        AppUpdateConfig.TryParse(yaml).Should().BeNull();
    }

    [Fact]
    public void Quoted_values_are_unquoted()
    {
        AppUpdateConfig.TryParse("provider: github\nowner: 'a'\nrepo: \"b\"\nupdaterCacheDirName: '@scope-updater'\n")
            .Should().Be(new AppUpdateConfig("a", "b", "@scope-updater"));
    }

    [Fact]
    public void The_pinned_config_points_at_one_release_and_keeps_the_cache_folder()
    {
        var pinned = new AppUpdateConfig("Into-The-Latent", "DiffusionNexus.Installer", "diffusion-nexus-installer-updater").PinnedTo("v3.1.0");

        pinned.Should().Contain("provider: generic");
        pinned.Should().Contain("url: https://github.com/Into-The-Latent/DiffusionNexus.Installer/releases/download/v3.1.0");
        // Without it electron-updater logs an error and downloads into a folder named after the app.
        pinned.Should().Contain("updaterCacheDirName: 'diffusion-nexus-installer-updater'");
    }
}
