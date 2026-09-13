using System.Reflection;
using DiffusionNexus.Installer.Electron.Services;
using FluentAssertions;
using Xunit;

namespace DiffusionNexus.Installer.Tests.Services;

/// <summary>
/// The one version accessor the top bar, the updater page and the feedback report all read.
/// </summary>
public class AppVersionTests
{
    [Theory]
    [InlineData("3.1.0+9f6dc2f", "3.1.0")]
    [InlineData("3.0.5+abcdef0123456789", "3.0.5")]
    [InlineData("3.0.5-preview.2+abcdef0", "3.0.5-preview.2")]
    public void Drops_build_metadata_from_a_version_that_carries_it(string raw, string expected)
    {
        // Tested against a value that ACTUALLY has a suffix, because this build cannot produce
        // one: Directory.Build.props sets IncludeSourceRevisionInInformationalVersion=false, so a
        // "the rendered version contains no +" assertion passes whether the stripping exists or
        // not. This one fails the moment the stripping is removed.
        AppVersion.Strip(raw).Should().Be(expected);
    }

    [Theory]
    [InlineData("3.0.5")]
    [InlineData("3.0.5-preview.2")]
    public void Leaves_a_version_without_build_metadata_exactly_as_it_is(string raw)
    {
        AppVersion.Strip(raw).Should().Be(raw);
    }

    [Fact]
    public void Says_unknown_rather_than_throwing_when_the_assembly_carries_no_version()
    {
        AppVersion.Strip(null).Should().Be("unknown");
    }

    [Fact]
    public void Reports_this_builds_own_version_with_that_same_stripping_applied()
    {
        // Ties Display to Strip: the theories above pin the behaviour, this pins that Display is
        // what the theories describe rather than a second, divergent copy of the two lines.
        var raw = typeof(AppVersion).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;

        AppVersion.Display.Should().NotBeNullOrWhiteSpace();
        AppVersion.Display.Should().Be(AppVersion.Strip(raw));
    }
}
