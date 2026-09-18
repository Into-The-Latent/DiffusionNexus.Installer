using DiffusionNexus.Installer.Electron.Services;
using FluentAssertions;
using Xunit;

namespace DiffusionNexus.Installer.Tests.Host;

public class ReturnTargetTests
{
    [Fact]
    public void Leads_to_the_welcome_screen_until_a_flow_screen_has_been_seen()
        => new ReturnTarget().Path.Should().Be("/");

    [Theory]
    [InlineData("", "/")]
    [InlineData("software/ComfyUI", "/software/ComfyUI")]
    [InlineData("/install/abc", "/install/abc")]
    [InlineData("install/abc?x=1", "/install/abc")]
    [InlineData("install/abc#log", "/install/abc")]
    public void Remembers_a_rooted_path_and_nothing_else(string baseRelative, string expected)
    {
        var target = new ReturnTarget();

        target.Remember(baseRelative);

        target.Path.Should().Be(expected);
    }

    [Fact]
    public void Knows_whether_a_page_is_being_returned_to_or_arrived_at()
    {
        var target = new ReturnTarget();
        target.Remember("install/ABC");

        target.IsAt("install/abc?x=1").Should().BeTrue("route matching is case-insensitive and ignores the query");
        target.IsAt("software/ComfyUI").Should().BeFalse();
    }
}
